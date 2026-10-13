using System;
using System.Collections.Generic;
using System.Net;
namespace NatTest.Common
{
    public sealed class Options
    {
        private readonly Dictionary<string,string> values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public Options(string[] args, IDictionary<string,string> defaults) { foreach (var p in defaults) values[p.Key]=p.Value; for (int i=0;i<args.Length;i++) { if (!args[i].StartsWith("--") || i+1==args.Length) throw new ArgumentException("Expected --option value"); values[args[i].Substring(2)]=args[++i]; } }
        public string Get(string key) { return values[key]; }
        public int Int(string key,int min,int max) { int n; if (!int.TryParse(Get(key),out n)||n<min||n>max) throw new ArgumentException("Invalid --"+key); return n; }
        public static IPEndPoint Resolve(string host,int port) { IPAddress ip; if (!IPAddress.TryParse(host,out ip)) { IPAddress[] a=Dns.GetHostAddresses(host); if(a.Length==0) throw new InvalidOperationException("DNS returned no addresses for "+host); ip=a[0]; } return new IPEndPoint(ip,port); }
    }
}
