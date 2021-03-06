using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using GameNet;
using P2pNet;

namespace Apian
{
    public interface IApianGameNet : IGameNet
    {
        void AddApianInstance( ApianBase instance, string groupId);
        void RequestGroups();
        void SendApianMessage(string toChannel, ApianMessage appMsg);
        ApianMessage DeserializeApianMessage(string msgType, string msgJSON);
        PeerClockSyncData GetP2pPeerClockSyncData(string P2pPeerId);

        // TODO: Are these needed?
        //void CreateApianGroup(ApianGroupInfo groupInfo); // TODO: results in An ApianGroupData message on game ch
        //void JoinApianGroup(string groupId); // results in An Apian MemberJoinedGroup message on group ch
        //void LeaveApianGroup(string groupId); // results in An Apian GroupMemberStatus message on group ch
    }

    public interface IApianApplication : IGameNetClient
    {
        // This is the "backend" part of an Apian app
        // which sets up GameNet (and probably the GameInstance/Apian pairs)
        // and that handles any stuff (chat messages, etc)  not Apian-related
        void OnGroupAnnounce(string groupId, string groupType, string creatorId, string groupName);
        void AddAppCore(IApianAppCore coreInstance);
    }

    public class ApianNetworkPeer
    {
        // This is a GameNet/P2pNet peer. There is only one of these, no matter
        // how many ApianInstances/Groups there are.

        public string P2pId;
        public string P2NetpHelloData; // almost always JSON

        public ApianNetworkPeer(string p2pId, string helloData)
        {
            P2pId = p2pId;
            P2NetpHelloData = helloData;
        }
    }

    public abstract class ApianGameNetBase : GameNetBase, IApianGameNet
    {
        // This is the actual GameNet instance
        public IApianApplication gameManager; // This is the IGameNetClient
        public Dictionary<string,ApianNetworkPeer> Peers; // keyed by p2pid
        public Dictionary<string, ApianBase> ApianInstances; // keyed by groupId

        protected Dictionary<string, Action<string, string, long, GameNetClientMessage>> _MsgDispatchers;
        public class GameCreationData {}

        public ApianGameNetBase() : base()
        {
            ApianInstances = new Dictionary<string, ApianBase>();
            Peers = new Dictionary<string,ApianNetworkPeer>();

            _MsgDispatchers = new  Dictionary<string, Action<string, string, long, GameNetClientMessage>>()
            {
                [ApianMessage.CliRequest] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliObservation] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliCommand] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.ApianClockOffset] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.GroupMessage] = (f,t,s,m) => this._DispatchGroupMessage(f,t,s,m),
            };
        }

        //
        // *** IGameNet
        //

        // void Connect( string p2pConectionString );

        public override void SetClient(IGameNetClient _client)
        {
            base.SetClient(_client);
            gameManager = _client as IApianApplication;
        }

        // void Disconnect();

        public override void  CreateGame<GameCreationData>(GameCreationData data)
        {
            logger.Verbose($"CreateGame()");
            _SyncTrivialNewGame(); // Creates/sets an ID and enqueues OnGameCreated()
        }

        // void JoinGame(string gameP2pChannel); // calls OnPeerJoined() - override that

        public override void LeaveGame()
        {
            // needs to clean up ApianInstances
            ApianInstances.Clear();
            base.LeaveGame();
        }

        // void AddChannel(string subChannel);

        // void RemoveChannel(string subchannel);

        // string LocalP2pId();

        // string CurrentGameId();

        // void Loop();

        //
        //  *** IP2pNetClient
        //

        //public virtual void JoinGame(string gameP2pChannel)

        // public virtual void LeaveGame()

        // string P2pHelloData(); // Hello data FOR remote peer. Probably JSON-encoded by the p2pnet client.

        public override void OnPeerJoined(string p2pId, string helloData)
        {
            // This means a peer joined the main Game channel.
            Peers[p2pId] = new ApianNetworkPeer(p2pId, helloData);
            base.OnPeerJoined(p2pId, helloData); // inform GameManager
        }

        public override void OnPeerLeft(string p2pId)
        {
            // P2pNet Peer left main game channel. Hopefully any Apian instances it was part of already know
            // Send that it's gone, just in case.
            foreach (ApianBase ap in ApianInstances.Values)
                ap.OnApianMessage( LocalP2pId(), ap.GroupId, new GroupMemberStatusMsg(ap.GroupId, p2pId, ApianGroupMember.Status.Removed), 0);

            base.OnPeerLeft(p2pId); // for gamemgr

            Peers.Remove(p2pId);
        }

        public override void OnPeerSync(string p2pId, long clockOffsetMs, long netLagMs)
        {
            // Should not go to gamenet client (gameManager - it will have to implement a stub handler anyway
            // since its part of IGameNetClient. Maybe it shouldn't be?)
            // Instead should go to ApianInstances
            foreach (ApianBase ap in ApianInstances.Values)
                ap.OnP2pPeerSync(p2pId, clockOffsetMs, netLagMs);
        }

        // void OnClientMsg(string from, string to, long msSinceSent, string payload);

        //
        // *** Additional ApianGameNet stuff
        //

        public PeerClockSyncData GetP2pPeerClockSyncData(string p2pPeerId)
        {
            return p2p.GetPeerClockSyncData(p2pPeerId);
        }

        public void AddApianInstance( ApianBase instance, string groupId)
        {
            ApianInstances[groupId] = instance;
        }

        public void RequestGroups()
        {
            logger.Verbose($"RequestApianGroups()");
            SendApianMessage( CurrentGameId(),  new GroupsRequestMsg());
        }

        public void SendApianMessage(string toChannel, ApianMessage appMsg)
        {
            logger.Verbose($"SendApianMessage() - type: {appMsg.MsgType}, To: {toChannel}");
            _SendClientMessage( toChannel, appMsg.MsgType,  JsonConvert.SerializeObject(appMsg));
        }

        protected override void _HandleClientMessage(string from, string to, long msSinceSent, GameNetClientMessage msg)
        {
            // This is called by GameNetBase.OnClientMessage()
            // We want to pass messages through a dispatch table.
            // Turns out (for now, anyway) we're best-off letting it throw rather than handling exceptions
            _MsgDispatchers[msg.clientMsgType](from, to, msSinceSent, msg);
        }

        protected void _DispatchApianMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            ApianMessage apMsg = DeserializeApianMessage(clientMessage.clientMsgType,clientMessage.payload);
            logger.Verbose($"_DispatchApianMessage() Type: {apMsg.MsgType}, src: {(from==LocalP2pId()?"Local":from)}");

            if (ApianInstances.ContainsKey(apMsg.DestGroupId))
                ApianInstances[apMsg.DestGroupId].OnApianMessage( from,  to,  apMsg,  msSinceSent);

        }

        protected void _DispatchGroupMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            ApianGroupMessage apMsg = DeserializeApianMessage(clientMessage.clientMsgType,clientMessage.payload) as ApianGroupMessage;
            logger.Verbose($"_DispatchGroupMessage() Type: {apMsg.GroupMsgType}, Group: {apMsg.DestGroupId}, src: {(from==LocalP2pId()?"Local":from)}");

            if (ApianInstances.ContainsKey(apMsg.DestGroupId))
                ApianInstances[apMsg.DestGroupId].OnApianMessage( from,  to,  apMsg,  msSinceSent);
            else if (apMsg.DestGroupId == "") // It's a group message not sent to a particular group.
            {
                // TODO: This is kinda ugly, but sorta special-case. Still ugly, tho.
                switch(apMsg.GroupMsgType)
                {
                case ApianGroupMessage.GroupAnnounce:
                    GroupAnnounceMsg gaMsg = apMsg as GroupAnnounceMsg;
                    gameManager.OnGroupAnnounce(gaMsg.GroupId, gaMsg.GroupType, gaMsg.GroupCreatorId, gaMsg.GroupName);
                    break;

                case ApianGroupMessage.GroupsRequest: // Send to all instances
                    foreach (ApianBase ap in ApianInstances.Values)
                        ap.OnApianMessage( from,  to,  apMsg,  msSinceSent);
                    break;
                }
            }
        }

        public abstract ApianMessage DeserializeApianMessage(string msgType, string msgJSON);
    }

}