﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NSmartProxy.Database;
using NSmartProxy.Infrastructure;

namespace NSmartProxy.Authorize
{
    public class AuthResult
    {
        public bool IsSuccess { get => ResultState == AuthState.Success; }
        public string ErrorMessage { get; set; }
        public AuthState ResultState { get; set; }
    }

    public static class TcpExt
    {
        /// <summary>
        /// return secureclient for valid
        /// </summary>
        /// <param name="listener"></param>
        /// <param name="dbOpSource">用来做安全校验的source</param>
        /// <returns></returns>
        public static async Task<SecurityTcpClient> AcceptSecureTcpClientAsync(this TcpListener listener,
            IDbOperator dbOpSource)
        {
            TcpClient obj = await listener.AcceptTcpClientAsync();
            var stc = obj.WrapServer(dbOpSource);//包装成server端用的socket
            //SecurityTcpClient stc = (SecurityTcpClient)tcpListener;
            stc.ClientType = ClientTypeEnum.Server;
            stc.DbOp = dbOpSource;
            return stc;
        }

        public static SecurityTcpClient WrapClient(this TcpClient client,string secureToken)
        {
            return new SecurityTcpClient(secureToken, null, ClientTypeEnum.Client, client);
        }

        public static SecurityTcpClient WrapServer(this TcpClient client,IDbOperator dbOp)
        {
            return new SecurityTcpClient(null, dbOp, ClientTypeEnum.Server, client);
        }
    }

    public enum AuthState
    {
        Success,
        Fail,
        Timeout,
        Error
    }

    public enum ClientTypeEnum
    {
        Server,
        Client
    }
    /// <summary>
    /// 标识位        token长度    token值
    /// 2位固定0xF9   2            n
    /// 服务端需要指定持久化逻辑，客户端只需token
    /// </summary>
    public class SecurityTcpClient 
    {
        public IDbOperator DbOp;

        public string Token = "";
        public readonly byte F9 = 0xF9;//固定标识位
        public String ErrorMessage = "";
        //TODO 是否校验
        public bool IsValid;
        public ClientTypeEnum ClientType;
        public TcpClient Client;

        /// <summary>
        /// 客户端使用这个来初始化
        /// </summary>
        /// <param name="secureToken"></param>
        /// <param name="dbOp"></param>
        public SecurityTcpClient(string secureToken, IDbOperator dbOp, ClientTypeEnum clientType,TcpClient client) 
        {
            Token = secureToken;
            DbOp = dbOp;
            ClientType = clientType;
            Client = client;
        }

       

     


        /// <summary>
        /// 带加密串传输
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <returns></returns>
        public async Task ConnectWithAuthAsync(string host, int port)
        {
            if (String.IsNullOrEmpty(Token))
            {
                return;
            }

            //标识位 token长度 值
            int requestLength = 1 + 2 + Token.Length;
            await Client.ConnectAsync(host, port);
            var stream = Client.GetStream();
            //await base.ConnectAsync(host, port);
            await stream.WriteAsync(new byte[] { F9 }, 0, 1);//1标识 长度1
            await stream.WriteAsync(StringUtil.IntTo2Bytes(Token.Length), 0, 2);//2token长度 长度2
            await stream.WriteAndFlushAsync(ASCIIEncoding.ASCII.GetBytes(Token));//3token
        }



        /// <summary>
        /// 服务端校验
        /// </summary>
        /// <returns></returns>
        public async Task<AuthResult> AuthorizeAsync()
        {
            var stream = Client.GetStream();
            //标识 1
            var protocolBytes = new byte[1];//ArrayPool<byte>.Shared.Rent(1);
            if (await stream.ReadAsync(protocolBytes, 0, protocolBytes.Length) == 0)
            {
                ErrorMessage += "读取到0字节，客户端已关闭？";
                return null;
            }
            if (protocolBytes[0] != F9)
            {
                ErrorMessage += $"非法头部 {protocolBytes[0]}";
            }

            //2.token长度 2
            var lengthBytes = new byte[2]; //ArrayPool<byte>.Shared.Rent(2);
            if (await stream.ReadAsync(lengthBytes, 0, lengthBytes.Length) == 0)
            {
                ErrorMessage += "读取到0字节，客户端已关闭？";
                return null;
            }
            int tokenLength = StringUtil.DoubleBytesToInt(lengthBytes);

            //3.token校验
            var tokenBytes = new byte[tokenLength];//ArrayPool<byte>.Shared.Rent(tokenLength);
            if (await stream.ReadAsync(tokenBytes, 0, tokenBytes.Length) == 0)
            {
                ErrorMessage += "获取token失败？";
                return null;
            }

            var token = ASCIIEncoding.ASCII.GetString(tokenBytes);
            //TODO ***校验Token
            if (token == "notoken")
            {
                return new AuthResult()
                {
                    ErrorMessage = "校验成功",
                    ResultState = AuthState.Success
                };
            }
            else
            {
                return new AuthResult()
                {
                    ErrorMessage = "校验失败",
                    ResultState = AuthState.Fail
                };
            }
        }

        public AuthResult AuthorizeToken(string token)
        {

            try
            {
                var res = new AuthResult();
                //keep your prikey safe!
                string userid = EncryptHelper.AES_Decrypt(token);
                //
                if (DbOp.Exist(userid))
                {
                    res.ResultState = AuthState.Success;
                }
                else
                {
                    res.ResultState = AuthState.Fail;
                }
                return res;
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    ResultState = AuthState.Error,
                    ErrorMessage = $"校验token出错：{ex.ToString()}"
                };
            }

        }
    }
}
