#region License
/*
 * SocketIO.cs
 *
 * The MIT License
 *
 * Copyright (c) 2014 Fabio Panettieri
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

#endregion

using SocketIO;
using System;
using System.Collections.Generic;
using System.Threading;
using WebSocketSharp;

namespace SocketIO
{
    public abstract class ThreadRunnerBase
    {
        volatile bool v_connected = true;
        Thread m_thread;

        public Action<Object> Debug = _ => { };
        public Action<Object> Info = _ => { };
        public Action<Object> Warning = _ => { };
        public Action<Object> Error = _ => { };            

        public void Start(Action threadWork, Action onExit=null)
        {
            m_thread = new Thread(()=>
            {
                while (v_connected)
                {
                    threadWork();
                }
                if (onExit != null)
                {
                    onExit();
                }
            });
            m_thread.Start();
        }

        public void Stop()
        {
            v_connected = false;
        }

        public void Abort()
        {
            Stop();
            if (m_thread != null)
            {
                m_thread.Abort();
                m_thread = null;
            }
        }
    }

    public class PingPong : ThreadRunnerBase
    {
        SocketIOSocket m_sio;
        volatile bool v_thPinging;
        volatile bool v_thPong;

        public PingPong(SocketIOSocket sio)
        {
            m_sio = sio;
            sio.On("pong", e =>
            {
                v_thPong = true;
                v_thPinging = false;
            });

            int reconnectDelay = 5;
            float pingInterval = 25f;
            float pingTimeout = 60f;
            int timeoutMilis = (int)Math.Floor(pingTimeout * 1000);
            int intervalMilis = (int)Math.Floor(pingInterval * 1000);

            Start(() =>
            {
                if (!m_sio.wsConnected)
                {
                    Thread.Sleep(reconnectDelay);
                }
                else
                {
                    v_thPinging = true;
                    v_thPong = false;

                    m_sio.EmitPacket(new Packet(EnginePacketType.PING));
                    var pingStart = DateTime.Now;

                    // Pongを待つ
                    while (m_sio.wsConnected
                        && v_thPinging
                        && (DateTime.Now - pingStart).TotalSeconds < timeoutMilis)
                    {
                        Thread.Sleep(200);
                    }

                    // Pongが来なかった
                    if (!v_thPong)
                    {
                        Warning("ping timeout. disconnect");
                        m_sio.Disconnect();
                    }

                    Thread.Sleep(intervalMilis);
                }
            });
        }
    }

    public class SocketIOSocket: ThreadRunnerBase
    {
        #region WebSocket
        Encoder encoder = new Encoder();
        WebSocket ws;
        bool m_wsConnected = false;
        public bool wsConnected
        {
            get
            {
                return m_wsConnected;
            }
            set
            {
                if (m_wsConnected == value) return;
                if (value)
                {
                    RaiseEvent("connect");
                }
                else
                {
                    RaiseEvent("disconnect");
                }
            }
        }

        Decoder decoder = new Decoder();
        Parser parser = new Parser();

        void OnMessage(object sender, MessageEventArgs e)
        {
            Info("[SocketIO]OnMessage#Raw message: " + e.Data);

            var packet = decoder.Decode(e);
            switch (packet.enginePacketType)
            {
                case EnginePacketType.OPEN:
                    {
                        Info("    [SocketIO]OnMessage#open sid: " + packet.json["sid"].str);

                        wsConnected = ws.IsConnected;
                        //sid = packet.json["sid"].str;
                        RaiseEvent("open");
                    }
                    break;

                case EnginePacketType.CLOSE:
                    wsConnected = ws.IsConnected;
                    RaiseEvent("close");
                    break;

                case EnginePacketType.PING:
                    EmitPacket(new Packet(EnginePacketType.PONG));
                    break;

                case EnginePacketType.PONG:
                    RaiseEvent("pong");
                    break;

                case EnginePacketType.MESSAGE:
                    if (packet.json == null)
                    {
                        Warning("  [SocketIO]null message");
                    }
                    else
                    {
                        if (packet.socketPacketType == SocketPacketType.ACK)
                        {
                            for (int i = 0; i < ackList.Count; i++)
                            {
                                if (ackList[i].packetId != packet.id) { continue; }
                                lock (ackQueueLock) { ackQueue.Enqueue(packet); }
                            }
                        }
                        else if (packet.socketPacketType == SocketPacketType.EVENT)
                        {
                            try
                            {
                                var ioe = parser.Parse(packet.json);
                                EnqueueEvent(ioe);
                            }
                            catch (SocketIOException ex)
                            {
                                Error(ex);
                            }
                        }
                    }
                    break;

                default:
                    Warning("    [SocketIO] unknown: " + packet.enginePacketType);
                    break;
            }
        }

        public void Emit(string ev)
        {
            EmitMessage(-1, string.Format("[\"{0}\"]", ev));
        }

        public void Emit(string ev, Action<JSONObject> action)
        {
            EmitMessage(++packetId, string.Format("[\"{0}\"]", ev));
            ackList.Add(new Ack(packetId, action));
        }

        public void Emit(string ev, JSONObject data)
        {
            EmitMessage(-1, string.Format("[\"{0}\",{1}]", ev, data));
        }

        public void Emit(string ev, JSONObject data, Action<JSONObject> action)
        {
            EmitMessage(++packetId, string.Format("[\"{0}\",{1}]", ev, data));
            ackList.Add(new Ack(packetId, action));
        }

        private void EmitMessage(int id, string raw)
        {
            EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.EVENT, 0, "/", id, new JSONObject(raw)));
        }

        private void EmitClose()
        {
            EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.DISCONNECT, 0, "/", -1, new JSONObject("")));
            EmitPacket(new Packet(EnginePacketType.CLOSE));
        }

        public void EmitPacket(Packet packet)
        {
            Info("[SocketIO]EmitPacket " + packet);

            try
            {
                ws.Send(encoder.Encode(packet));
            }
            catch (SocketIOException ex)
            {
                Error(ex.ToString());
            }
        }
        #endregion

        #region EventHandlers
        Dictionary<string, List<Action<SocketIOEvent>>> handlers
            = new Dictionary<string, List<Action<SocketIOEvent>>>();

        void RaiseEvent(string type)
        {
            RaiseEvent(new SocketIOEvent(type));
        }

        void RaiseEvent(SocketIOEvent ev)
        {
            if (!handlers.ContainsKey(ev.name)) { return; }
            foreach (Action<SocketIOEvent> handler in this.handlers[ev.name])
            {
                try
                {
                    handler(ev);
                }
                catch (Exception ex)
                {
                    Error(ex);
                }
            }
        }

        object eventQueueLock = new object();
        Queue<SocketIOEvent> eventQueue = new Queue<SocketIOEvent>();

        void EnqueueEvent(SocketIOEvent ioe)
        {
            lock (eventQueueLock) {
                eventQueue.Enqueue(ioe);
            }
        }                               

        void ProcessEvent()
        {
            lock (eventQueueLock)
            {
                while (eventQueue.Count > 0)
                {
                    RaiseEvent(eventQueue.Dequeue());
                }
            }
        }

        public void On(string ev, Action<SocketIOEvent> callback)
        {
            if (!handlers.ContainsKey(ev))
            {
                handlers[ev] = new List<Action<SocketIOEvent>>();
            }
            handlers[ev].Add(callback);
        }

        public void Off(string ev, Action<SocketIOEvent> callback)
        {
            if (!handlers.ContainsKey(ev))
            {
                Warning("[SocketIO] No callbacks registered for event: " + ev);
                return;
            }

            List<Action<SocketIOEvent>> l = handlers[ev];
            if (!l.Contains(callback))
            {
                Warning("[SocketIO] Couldn't remove callback action for event: " + ev);
                return;
            }

            l.Remove(callback);
            if (l.Count == 0)
            {
                handlers.Remove(ev);
            }
        }
        #endregion

        #region Ack
        int packetId;
        float ackExpirationTime = 1800f;
        List<Ack> ackList = new List<Ack>();

        object ackQueueLock = new object();
        Queue<Packet> ackQueue = new Queue<Packet>();

        void InvokeAck(Packet packet)
        {
            Ack ack;
            for (int i = 0; i < ackList.Count; i++)
            {
                if (ackList[i].packetId != packet.id) { continue; }
                ack = ackList[i];
                ackList.RemoveAt(i);
                ack.Invoke(packet.json);
                return;
            }
        }

        void ProcessAck()
        {
            lock (ackQueueLock)
            {
                while (ackQueue.Count > 0)
                {
                    InvokeAck(ackQueue.Dequeue());
                }
            }

            // GC expired acks
            if (ackList.Count == 0) { return; }
            if (DateTime.Now.Subtract(ackList[0].time).TotalSeconds < ackExpirationTime) { return; }
            ackList.RemoveAt(0);
        }
        #endregion

        public void Disconnect()
        {
            EmitClose();
            Stop();
        }

        public void Start(string url = "ws://127.0.0.1:3000/socket.io/?EIO=4&transport=websocket")
        { 
            ws = new WebSocket(url);
            ws.OnOpen += (s, e) => RaiseEvent("open");
            ws.OnError += (s, e)=> RaiseEvent("error");
            ws.OnClose += (s, e) => RaiseEvent("close");
            ws.OnMessage += OnMessage;

            int reconnectDelay = 500;

            base.Start(() =>
            {
                if (ws.IsConnected)
                {
                    Thread.Sleep(reconnectDelay);
                }
                else
                {
                    ws.Connect();
                }
            }
            , ()=>
            {
                ws.Close();
            });

        }

        public void Update()
        {
            wsConnected = ws.IsConnected;
            ProcessAck();
            ProcessEvent();
        }
    }
}
