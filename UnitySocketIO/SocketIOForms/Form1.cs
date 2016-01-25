using SocketIO;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SocketIOForms
{
    public partial class Form1 : Form
    {
        SocketIOSocket m_socket;

        public Form1()
        {
            InitializeComponent();

            m_socket = new SocketIOSocket();

            m_socket.Start();
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            if(m_socket!= null)
            {
                m_socket.Abort();
                m_socket = null;
            }
        }
    }
}
