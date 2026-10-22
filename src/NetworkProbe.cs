using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;

// Harmless connectivity probe with distinct protected/control executable paths.
// TCP sends no payload; UDP sends a fixed marker without account data.
class NetworkProbe {
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder name);
    static int Main(string[] args) {
        if (args.Length == 4) {
            using (StreamWriter output = new StreamWriter(args[3])) {
                Console.SetOut(output);
                int code=Run(args);
                uint len=512; var family=new StringBuilder(512);
                int identity=GetPackageFamilyName(GetCurrentProcess(),ref len,family);
                Console.WriteLine("{\"packageFamily\":\""+(identity==0?family.ToString():"none")+"\"}");
                return code;
            }
        }
        return Run(args);
    }
    static int Run(string[] args) {
        if ((args.Length != 3 && args.Length != 4) || (args[0] != "tcp" && args[0] != "udp")) {
            Console.Error.WriteLine("Usage: NetworkProbe tcp|udp numeric-address port"); return 2;
        }
        try {
            IPAddress address = IPAddress.Parse(args[1]);
            int port = int.Parse(args[2]);
            bool udp = args[0] == "udp";
            using (Socket socket = new Socket(address.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream,
                                              udp ? ProtocolType.Udp : ProtocolType.Tcp)) {
                socket.SendTimeout = 2500;
                socket.ReceiveTimeout = 2500;
                if (udp) {
                    socket.Connect(new IPEndPoint(address, port));
                    socket.Send(Encoding.ASCII.GetBytes("killswitch-test"));
                } else {
                    IAsyncResult result = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                    if (!result.AsyncWaitHandle.WaitOne(3000)) {
                        Console.WriteLine("{\"result\":\"timeout\"}"); return 3;
                    }
                    socket.EndConnect(result);
                }
                Console.WriteLine("{\"result\":\"connected-or-sent\"}"); return 0;
            }
        } catch (SocketException e) {
            Console.WriteLine("{\"result\":\"socket-error\",\"code\":" + e.ErrorCode + "}"); return 10;
        } catch (Exception e) {
            Console.WriteLine("{\"result\":\"error\",\"type\":\"" + e.GetType().Name + "\"}"); return 11;
        }
    }
}
