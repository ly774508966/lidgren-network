/* Copyright (c) 2008 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Lidgren.Network
{
	/// <summary>
	/// A server which can accept connections from multiple NetClients
	/// </summary>
	public class NetServer : NetBase
	{
		protected List<NetConnection> m_connections;
		protected Dictionary<IPEndPoint, NetConnection> m_connectionLookup;
		protected bool m_allowOutgoingConnections; // used by NetPeer

		public List<NetConnection> Connections
		{
			get
			{
				lock (m_connections)
				{
					return new List<NetConnection>(m_connections);
				}
			}
		}

		public NetServer(NetConfiguration config)
			: base(config)
		{
			m_connections = new List<NetConnection>();
			m_connectionLookup = new Dictionary<IPEndPoint, NetConnection>();
			m_messagePool = new NetPool<NetMessage>(256, 4);
			m_bufferPool = new NetPool<NetBuffer>(256, 4);
			m_lockedMessagePool = new NetQueue<NetMessage>();
			m_lockedBufferPool = new NetQueue<NetBuffer>();
		}
		
		/// <summary>
		/// Reads and sends messages from the network
		/// </summary>
		protected override void Heartbeat()
		{
			double now = NetTime.Now;

			if (m_shutdownRequested)
			{
				PerformShutdown(m_shutdownReason);
				return;
			}

			//
			// Drain locked pools
			//
			// m_messagePool and m_bufferPool is only accessed from this thread; thus no locking
			// is required for those objects
			//
			lock (m_lockedBufferPool)
			{
				NetBuffer lb;
				while ((lb = m_lockedBufferPool.Dequeue()) != null)
					m_bufferPool.Push(lb);
			}
			lock (m_lockedMessagePool)
			{
				NetMessage lm;
				while ((lm = m_lockedMessagePool.Dequeue()) != null)
					m_messagePool.Push(lm);
			}

			// read messages from network
			BaseHeartbeat(now);

			lock (m_connections)
			{
				List<NetConnection> deadConnections = null;
				foreach (NetConnection conn in m_connections)
				{
					if (conn.m_status == NetConnectionStatus.Disconnected)
					{
						if (deadConnections == null)
							deadConnections = new List<NetConnection>();
						deadConnections.Add(conn);
						continue;
					}

					conn.Heartbeat(now);
				}

				if (deadConnections != null)
				{
					foreach (NetConnection conn in deadConnections)
					{
						m_connections.Remove(conn);
						m_connectionLookup.Remove(conn.RemoteEndpoint);
					}
				}
			}
		}

		internal override NetConnection GetConnection(IPEndPoint remoteEndpoint)
		{
			NetConnection retval;
			if (m_connectionLookup.TryGetValue(remoteEndpoint, out retval))
				return retval;
			return null;
		}

		internal override void HandleReceivedMessage(NetMessage message, IPEndPoint senderEndpoint)
		{
			double now = NetTime.Now;

			int payLen = message.m_data.LengthBytes;

			if (message.m_sender == null)
			{
				// not a connected sender; only allow System messages
				if (message.m_type != NetMessageLibraryType.System)
				{
					if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
						NotifyApplication(NetMessageType.BadMessageReceived, "Rejecting non-system message from unconnected source: " + message, null);
					return;
				}

				// read type of system message
				NetSystemType sysType = (NetSystemType)message.m_data.ReadByte();
				switch (sysType)
				{
					case NetSystemType.Connect:

						LogVerbose("Connection request received");

						// check app ident
						if (payLen < 4)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Malformed Connect message received from " + senderEndpoint, null);
							return;
						}
						string appIdent = message.m_data.ReadString();
						if (appIdent != m_config.ApplicationIdentifier)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Connect for different application identification received: " + appIdent, null);
							return;
						}

						// read random identifer
						byte[] rnd = message.m_data.ReadBytes(8);
						if (NetUtility.CompareElements(rnd, m_randomIdentifier))
						{
							// don't allow self-connect
							if ((m_enabledMessageTypes & NetMessageType.ConnectionRejected) == NetMessageType.ConnectionRejected)
								NotifyApplication(NetMessageType.ConnectionRejected, "Connection to self not allowed", null);
							return;
						}

						int bytesReadSoFar = (message.m_data.Position / 8);
						int hailLen = message.m_data.LengthBytes - bytesReadSoFar;
						byte[] hailData = null;
						if (hailLen > 0)
						{
							hailData = new byte[hailLen];
							Buffer.BlockCopy(message.m_data.Data, bytesReadSoFar, hailData, 0, hailLen);
						}

						if (m_connections.Count >= m_config.m_maxConnections)
						{
							if ((m_enabledMessageTypes & NetMessageType.ConnectionRejected) == NetMessageType.ConnectionRejected)
								NotifyApplication(NetMessageType.ConnectionRejected, "Server full; rejecting connect from " + senderEndpoint, null);
							return;
						}

						// Create connection
						LogWrite("New connection: " + senderEndpoint);
						NetConnection conn = new NetConnection(this, senderEndpoint, hailData);

						// Connection approval?
						if ((m_enabledMessageTypes & NetMessageType.ConnectionApproval) == NetMessageType.ConnectionApproval)
						{
							// Ask application if this connection is allowed to proceed
							NetMessage app = CreateMessage();
							app.m_msgType = NetMessageType.ConnectionApproval;
							app.m_data.Write(hailData);
							app.m_sender = conn;
							conn.m_approved = false;
							lock (m_receivedMessages)
								m_receivedMessages.Enqueue(app);
							// Don't add connection; it's done as part of the approval procedure
							return;
						}

						// it's ok
						AddConnection(now, conn);
						break;
					case NetSystemType.ConnectionEstablished:
						if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
							NotifyApplication(NetMessageType.BadMessageReceived, "Connection established received from non-connection! " + senderEndpoint, null);
						return;
					case NetSystemType.Discovery:
						// check app ident
						if (payLen < 5)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Malformed Discovery message received from " + senderEndpoint, null);
							return;
						}
						string appIdent2 = message.m_data.ReadString();
						if (appIdent2 != m_config.ApplicationIdentifier)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Discovery for different application identification received: " + appIdent2, null);
							return;
						}

						// send discovery response
						SendDiscoveryResponse(senderEndpoint);
						break;
					default:
						if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
							NotifyApplication(NetMessageType.BadMessageReceived, "Undefined behaviour for " + this + " receiving system type " + sysType + ": " + message + " from unconnected source", null);
						break;
				}
				// done
				return;
			}

			// ok, we have a sender
			if (message.m_type == NetMessageLibraryType.Acknowledge)
			{
				message.m_sender.HandleAckMessage(message);
				return;
			}

			if (message.m_type == NetMessageLibraryType.System)
			{
				// handle system messages from connected source
				if (payLen < 1)
				{
					if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
						NotifyApplication(NetMessageType.BadMessageReceived, "Received malformed system message; payload length less than 1 byte", null);
					return;
				}
				NetSystemType sysType = (NetSystemType)message.m_data.ReadByte();
				switch (sysType)
				{
					case NetSystemType.Connect:
					case NetSystemType.ConnectionEstablished:
					case NetSystemType.Ping:
					case NetSystemType.Pong:
					case NetSystemType.Disconnect:
						message.m_sender.HandleSystemMessage(message, now);
						break;
					case NetSystemType.ConnectResponse:
						if (m_allowOutgoingConnections)
						{
							message.m_sender.HandleSystemMessage(message, now);
						}
						else
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Undefined behaviour for server and system type " + sysType, null);
						}
						break;
					case NetSystemType.Discovery:
						// Allow discovery even if connected
						// check app ident
						if (payLen < 5)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Malformed Discovery message received", null);
							return;
						}
						string appIdent2 = message.m_data.ReadString();
						if (appIdent2 != m_config.ApplicationIdentifier)
						{
							if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
								NotifyApplication(NetMessageType.BadMessageReceived, "Discovery for different application identification received: " + appIdent2, null);
							return;
						}

						// make sure we didn't send this request ourselves (in case of NetPeer)
						if (IPAddress.IsLoopback(senderEndpoint.Address) && senderEndpoint.Port == this.ListenPort)
							break;

						// send discovery response
						SendDiscoveryResponse(senderEndpoint);
						break;
					default:
						if ((m_enabledMessageTypes & NetMessageType.BadMessageReceived) == NetMessageType.BadMessageReceived)
							NotifyApplication(NetMessageType.BadMessageReceived, "Undefined behaviour for server and system type " + sysType, null);
						break;
				}
				return;
			}

			if (message.m_type == NetMessageLibraryType.UserFragmented)
				throw new NotImplementedException();

			// Must be user message at this point
			Debug.Assert(message.m_type == NetMessageLibraryType.User);

			message.m_sender.HandleUserMessage(message);
		}

		internal void AddConnection(double now, NetConnection conn)
		{
			// send response; even if connected
			NetMessage response = CreateSystemMessage(NetSystemType.ConnectResponse);
			conn.m_unsentMessages.Enqueue(response);

			if (conn.m_status != NetConnectionStatus.Connecting)
				conn.SetStatus(NetConnectionStatus.Connecting, "Connecting...");

			conn.m_handshakeInitiated = now;
			
			conn.m_approved = true;
			lock (m_connections)
				m_connections.Add(conn);
			m_connectionLookup.Add(conn.m_remoteEndPoint, conn);
		}

		/*
		/// <summary>
		/// Read any received message in any connection queue
		/// </summary>
		public NetBuffer ReadMessage(out NetConnection sender)
		{
			if (m_receivedMessages.Count < 1)
			{
				sender = null;
				return null;
			}

			NetMessage msg = m_receivedMessages.Dequeue();
			sender = msg.m_sender;

			NetBuffer retval = msg.m_data;
			msg.m_data = null;
			m_messagePool.Push(msg);

			Debug.Assert(retval.Position == 0);

			return retval;
		}
		*/

		/// <summary>
		/// Read any received message in any connection queue
		/// </summary>
		public bool ReadMessage(NetBuffer intoBuffer, List<NetConnection> onlyFor, bool includeNullConnectionMessages, out NetMessageType type, out NetConnection sender)
		{
			NetMessage msg = null;
			lock (m_receivedMessages)
			{
				int sz = m_receivedMessages.Count;
				for (int i = 0; i < sz; i++)
				{
					msg = m_receivedMessages.Peek(i);
					if (msg != null)
					{
						if ((msg.m_sender == null && includeNullConnectionMessages) ||
							onlyFor.Contains(msg.m_sender))
						{
							m_receivedMessages.Dequeue(i);
							break;
						}
						msg = null;
					}
				}
			}

			if (msg == null)
			{
				sender = null;
				type = NetMessageType.None;
				return false;
			}

			sender = msg.m_sender;

			// recycle NetMessage object
			NetBuffer content = msg.m_data;
			msg.m_data = null;
			type = msg.m_msgType;

			lock (m_lockedMessagePool)
				m_lockedMessagePool.Enqueue(msg);

			Debug.Assert(content.Position == 0);

			// swap content of buffers
			byte[] tmp = intoBuffer.Data;
			intoBuffer.Data = content.Data;
			content.Data = tmp;

			// set correct values for returning value (ignore the other, it's being recycled anyway)
			intoBuffer.m_bitLength = content.m_bitLength;
			intoBuffer.m_readPosition = 0;

			// recycle NetBuffer object (incl. old intoBuffer byte array)
			content.m_refCount = 0;

			lock (m_lockedBufferPool)
				m_lockedBufferPool.Enqueue(content);

			return true;
		}

		/// <summary>
		/// Read any received message in any connection queue
		/// </summary>
		public bool ReadMessage(NetBuffer intoBuffer, out NetMessageType type, out NetConnection sender)
		{
			NetMessage msg;
			lock(m_receivedMessages)
				msg = m_receivedMessages.Dequeue();

			if (msg == null)
			{
				sender = null;
				type = NetMessageType.None;
				return false;
			}

#if DEBUG
			if (msg.m_data == null)
				throw new NetException("Ouch!");
			if (msg.m_data.Position != 0)
				throw new NetException("Ouch!");
#endif
			
			sender = msg.m_sender;

			// recycle NetMessage object
			NetBuffer content = msg.m_data;

			msg.m_data = null;
			type = msg.m_msgType;

			// swap content of buffers
			byte[] tmp = intoBuffer.Data;
			intoBuffer.Data = content.Data;
			content.Data = tmp;

			// set correct values for returning value (ignore the other, it's being recycled anyway)
			intoBuffer.m_bitLength = content.m_bitLength;
			intoBuffer.m_readPosition = 0;

			// recycle NetBuffer object (incl. old intoBuffer byte array)
			content.m_refCount = 0;

			// put new message in frontend queue
			lock (m_lockedMessagePool)
				m_lockedMessagePool.Enqueue(msg);

			// recycle content
			lock (m_lockedBufferPool)
				m_lockedBufferPool.Enqueue(content);

			return true;
		}

		/// <summary>
		/// Sends a message to a specific connection
		/// </summary>
		public void SendMessage(NetBuffer data, NetConnection recipient, NetChannel channel)
		{
			if (recipient == null)
				throw new ArgumentNullException("recipient");
			recipient.SendMessage(data, channel);
		}

		/// <summary>
		/// Sends a message to the specified connections
		/// </summary>
		public void SendMessage(NetBuffer data, IList<NetConnection> recipients, NetChannel channel)
		{
			if (recipients == null)
				throw new ArgumentNullException("recipients");

			foreach (NetConnection recipient in recipients)
				recipient.SendMessage(data, channel);
		}

		/// <summary>
		/// Sends a message to all connections to this server
		/// </summary>
		public void SendToAll(NetBuffer data, NetChannel channel)
		{
			lock (m_connections)
			{
				foreach (NetConnection conn in m_connections)
				{
					if (conn.Status == NetConnectionStatus.Connected)
						conn.SendMessage(data, channel);
				}
			}
		}

		/// <summary>
		/// Sends a message to all connections to this server, except 'exclude'
		/// </summary>
		public void SendToAll(NetBuffer data, NetChannel channel, NetConnection exclude)
		{
			lock (m_connections)
			{
				foreach (NetConnection conn in m_connections)
				{
					if (conn.Status == NetConnectionStatus.Connected && conn != exclude)
						conn.SendMessage(data, channel);
				}
			}
		}

		internal override void HandleConnectionForciblyClosed(NetConnection connection, SocketException sex)
		{
			if (connection != null)
				connection.Disconnect("Connection forcibly closed", 0, false);
			return;
		}

		private void SendDiscoveryResponse(IPEndPoint senderEndpoint)
		{
			m_scratchBuffer.Reset();

			SendSingleUnreliableSystemMessage(
				NetSystemType.DiscoveryResponse,
				m_scratchBuffer,
				senderEndpoint,
				false
			);
		}

		protected override void PerformShutdown(string reason)
		{
			foreach (NetConnection conn in m_connections)
				if (conn.m_status != NetConnectionStatus.Disconnected)
					conn.Disconnect(reason, 0, true);
			base.PerformShutdown(reason);
		}
	}
}
