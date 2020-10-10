using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Mirror.SimpleWeb
{
    /// <summary>
    /// Handles Handshakes from new clients on the server
    /// <para>The server handshake has buffers to reduce allocations when clients connect</para>
    /// </summary>
    internal class ServerHandshake
    {
        private const int ResponseLength = 129;
        private const int KeyLength = 24;
        const string KeyHeaderString = "Sec-WebSocket-Key: ";

        readonly object lockObj = new object();
        readonly byte[] readBuffer = new byte[3000];
        readonly byte[] keyBuffer = new byte[60];
        readonly byte[] response = new byte[ResponseLength];

        readonly SHA1 sha1 = SHA1.Create();

        public ServerHandshake()
        {
            // write string to buffer once here so we dont need to repeat it was each handshake
            Encoding.UTF8.GetBytes(Constants.HandshakeGUID, 0, Constants.HandshakeGUIDLength, keyBuffer, KeyLength);
        }
        ~ServerHandshake()
        {
            sha1.Dispose();
        }

        /// <summary>
        /// Clears buffers so that data can't be used by next request
        /// </summary>
        void ClearBuffers()
        {
            Array.Clear(readBuffer, 0, 300);
            Array.Clear(readBuffer, 0, 24);
            Array.Clear(response, 0, ResponseLength);
        }

        public bool TryHandshake(Connection conn)
        {
            TcpClient client = conn.client;
            Stream stream = conn.stream;

            try
            {
                byte[] getHeader = new byte[3];
                ReadHelper.ReadResult result = ReadHelper.SafeRead(stream, getHeader, 0, 3);
                if ((result & ReadHelper.ReadResult.Fail) > 0)
                    return false;

                if (!IsGet(getHeader))
                {
                    Log.Warn($"First bytes from client was not 'GET' for handshake, instead was {string.Join("-", getHeader.Select(x => x.ToString()))}");
                    return false;
                }
            }
            catch (Exception e) { Debug.LogException(e); return false; }

            // lock so that buffers can only be used by this thread
            lock (lockObj)
            {
                try
                {
                    //return BatchReadsForHandshake(stream);
                    bool success = ReadToEndForHandshake(stream);
                    if (success)
                        Log.Info($"Sent Handshake {conn}");
                    return success;
                    //return ReadAvailableForHandsake(client, stream);
                }
                catch (Exception e) { Debug.LogException(e); return false; }
                finally
                {
                    ClearBuffers();
                }
            }
        }

        private bool ReadToEndForHandshake(Stream stream)
        {
            int? readCountOrFail = ReadHelper.SafeReadTillMatch(stream, readBuffer, 0, Constants.endOfHandshake);
            if (!readCountOrFail.HasValue)
                return false;

            int readCount = readCountOrFail.Value;

            string msg = Encoding.UTF8.GetString(readBuffer, 0, readCount);

            AcceptHandshake(stream, msg);
            return true;
        }

        bool IsGet(byte[] getHeader)
        {
            // just check bytes here instead of using Encoding.UTF8
            return getHeader[0] == 71 && // G
                   getHeader[1] == 69 && // E
                   getHeader[2] == 84;   // T
        }

        void AcceptHandshake(Stream stream, string msg)
        {
            GetKey(msg, keyBuffer);
            CreateResponse();

            stream.Write(response, 0, ResponseLength);
        }

        void CreateResponse()
        {
            byte[] keyHash = sha1.ComputeHash(keyBuffer);

            string keyHashString = Convert.ToBase64String(keyHash);
            // compiler should merge these strings into 1 string before format
            string message = string.Format(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: {0}\r\n\r\n",
                keyHashString);

            Encoding.UTF8.GetBytes(message, 0, ResponseLength, response, 0);
        }

        static void GetKey(string msg, byte[] keyBuffer)
        {
            int start = msg.IndexOf(KeyHeaderString) + KeyHeaderString.Length;

            Encoding.UTF8.GetBytes(msg, start, KeyLength, keyBuffer, 0);
        }
    }
}