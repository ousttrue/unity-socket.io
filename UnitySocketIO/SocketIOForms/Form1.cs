using SocketIO;
using System;
using System.Windows.Forms;

namespace SocketIOForms
{
    public partial class Form1 : Form
    {
        Timer m_timer;
        SocketIOSocket m_socket;
        PingPong m_ping;

        public Form1()
        {
            InitializeComponent();

            m_socket = new SocketIOSocket();
            m_socket.Debug = Console.WriteLine;
            m_socket.Info = Console.WriteLine;
            m_socket.Warning = Console.WriteLine;
            m_socket.Error = Console.WriteLine;

            m_socket.On("open", Console.WriteLine);
            m_socket.On("message", Console.WriteLine);
            m_socket.On("close", Console.WriteLine);
            m_socket.On("error", Console.WriteLine);

            m_socket.Start();

            m_ping = new PingPong(m_socket);

            m_timer = new Timer();
            m_timer.Interval = 33;
            m_timer.Tick += (o, e) =>
              {
                  m_socket.Update();
              };
            m_timer.Start();
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            if(m_ping!= null)
            {
                m_ping.Abort();
                m_ping = null;
            }
            if(m_socket!= null)
            {
                m_socket.Abort();
                m_socket = null;
            }
        }
    }
}
