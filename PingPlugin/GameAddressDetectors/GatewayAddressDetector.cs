using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Dalamud.Logging;

namespace PingPlugin
{
    public class GatewayAddressDetector
    {
        public IPAddress GetAddress(IPAddress destinationAddress, bool verbose = false)
        {
            UInt32 destAddr = BitConverter.ToUInt32(destinationAddress.GetAddressBytes(), 0);

            var result = GetBestInterface(destAddr, out var interfaceIndex);
            if (result != (uint) WinError.NO_ERROR)
                return IPAddress.Loopback;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var niProps = ni.GetIPProperties();
                
                var gateway = niProps.GatewayAddresses?.FirstOrDefault()?.Address;
                if (gateway == null)
                    continue;

                if (ni.Supports(NetworkInterfaceComponent.IPv4))
                {
                    var v4Props = niProps.GetIPv4Properties();

                    if (v4Props.Index == interfaceIndex)
                    {
                        if (verbose && !Equals(gateway, IPAddress.Loopback))
                        {
                            PluginLog.Log($"Detected newly-connected FFXIV server address {gateway}");
                        }
                        return gateway;
                    }
                }
            }

            return IPAddress.Loopback;
        }

        [DllImport("iphlpapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetBestInterface(UInt32 destAddr, out UInt32 bestIfIndex);
    }
}