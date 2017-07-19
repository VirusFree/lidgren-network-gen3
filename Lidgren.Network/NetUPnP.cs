﻿using System;
using System.IO;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

#if !__NOIPENDPOINT__
using NetEndPoint = System.Net.IPEndPoint;
#endif

namespace Lidgren.Network
{
	/// <summary>
	/// Status of the UPnP capabilities
	/// </summary>
	public enum UPnPStatus
	{
		/// <summary>
		/// Still discovering UPnP capabilities
		/// </summary>
		Discovering,

		/// <summary>
		/// UPnP is not available
		/// </summary>
		NotAvailable,

		/// <summary>
		/// UPnP is available and ready to use
		/// </summary>
		Available
	}

	/// <summary>
	/// UPnP support class
	/// </summary>
	public class NetUPnP
	{
		private const int c_discoveryTimeOutMillis = 1000;

		class discoveryResult
		{
			public NetEndPoint sender;
			public string m_serviceUrl = "";
			public string m_serviceName = "";
		}
		List<discoveryResult> DiscoveryResults = new List<discoveryResult>();
		private NetPeer m_peer;
		private ManualResetEvent m_discoveryComplete = new ManualResetEvent(false);

		internal double m_discoveryResponseDeadline;

		private UPnPStatus m_status;

		/// <summary>
		/// Status of the UPnP capabilities of this NetPeer
		/// </summary>
		public UPnPStatus Status { get { return m_status; } }

		/// <summary>
		/// NetUPnP constructor
		/// </summary>
		public NetUPnP(NetPeer peer)
		{
			m_peer = peer;
			m_discoveryResponseDeadline = double.MinValue;
		}

		internal void Discover(NetPeer peer)
		{
			string str =
"M-SEARCH * HTTP/1.1\r\n" +
"HOST: 239.255.255.250:1900\r\n" +
"ST:upnp:rootdevice\r\n" +
"MAN:\"ssdp:discover\"\r\n" +
"MX:3\r\n\r\n";

			m_discoveryResponseDeadline = NetTime.Now + 6.0; // arbitrarily chosen number, router gets 6 seconds to respond
			m_status = UPnPStatus.Discovering;

			byte[] arr = System.Text.Encoding.UTF8.GetBytes(str);

			m_peer.LogDebug("Attempting UPnP discovery");
			peer.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
			foreach(var addr in NetUtility.GetAllBroadcastAddress())
				peer.RawSend(arr, 0, arr.Length, new NetEndPoint(addr, 1900));
			peer.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, false);
		}

		internal void CheckForDiscoveryTimeout()
		{
			if (NetTime.Now < m_discoveryResponseDeadline)
				return;

			lock (DiscoveryResults)
				m_status = DiscoveryResults.Count > 0 ? UPnPStatus.Available : UPnPStatus.NotAvailable;

			m_discoveryComplete.Set();
			m_peer.LogDebug("UPnP service ready");
		}

		internal void ExtractServiceUrl(NetEndPoint sender, string resp)
		{
#if !DEBUG
			try
			{
#endif
			XmlDocument desc = new XmlDocument();
			using (var response = WebRequest.Create(resp).GetResponse())
				desc.Load(response.GetResponseStream());

			XmlNamespaceManager nsMgr = new XmlNamespaceManager(desc.NameTable);
			nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
			XmlNode typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
			if (!typen.Value.Contains("InternetGatewayDevice"))
				return;

			var m_serviceName = "WANIPConnection";
			XmlNode node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" + m_serviceName + ":1\"]/tns:controlURL/text()", nsMgr);
			if (node == null)
			{
				//try another service name
				m_serviceName = "WANPPPConnection";
				node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:" + m_serviceName + ":1\"]/tns:controlURL/text()", nsMgr);
				if (node == null)
					return;
			}

			//add to results
			lock (DiscoveryResults)
				DiscoveryResults.Add(new discoveryResult()
				{
					m_serviceName = m_serviceName,
					m_serviceUrl = CombineUrls(resp, node.Value),
					sender = sender,
				});
#if !DEBUG
			}
			catch
			{
				m_peer.LogVerbose("Exception ignored trying to parse UPnP XML response");
				return;
			}
#endif
		}

		private static string CombineUrls(string gatewayURL, string subURL)
		{
			// Is Control URL an absolute URL?
			if ((subURL.Contains("http:")) || (subURL.Contains(".")))
				return subURL;

			gatewayURL = gatewayURL.Replace("http://", "");  // strip any protocol
			int n = gatewayURL.IndexOf("/");
			if (n != -1)
				gatewayURL = gatewayURL.Substring(0, n);  // Use first portion of URL
			return "http://" + gatewayURL + subURL;
		}

		private bool CheckAvailability()
		{
			switch (m_status)
			{
				case UPnPStatus.NotAvailable:
					return false;
				case UPnPStatus.Available:
					return true;
				case UPnPStatus.Discovering:
					if (m_discoveryComplete.WaitOne(c_discoveryTimeOutMillis))
						return true;
					if (NetTime.Now > m_discoveryResponseDeadline)
						m_status = UPnPStatus.NotAvailable;
					return false;
			}
			return false;
		}

		/// <summary>
		/// Add a forwarding rule to the router using UPnP
		/// </summary>
		public bool ForwardPort(int port, string description)
		{
			if (!CheckAvailability())
				return false;

			lock (DiscoveryResults)
				foreach (var result in DiscoveryResults)
				{
					IPAddress mask;
					var client = NetUtility.GetMyAddress(result.sender.Address);
					if (client == null)
						continue;

					try
					{
						SOAPRequest(result.m_serviceUrl,
							"<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + result.m_serviceName + ":1\">" +
							"<NewRemoteHost></NewRemoteHost>" +
							"<NewExternalPort>" + port.ToString() + "</NewExternalPort>" +
							"<NewProtocol>" + ProtocolType.Udp.ToString().ToUpper(System.Globalization.CultureInfo.InvariantCulture) + "</NewProtocol>" +
							"<NewInternalPort>" + port.ToString() + "</NewInternalPort>" +
							"<NewInternalClient>" + client.ToString() + "</NewInternalClient>" +
							"<NewEnabled>1</NewEnabled>" +
							"<NewPortMappingDescription>" + description + "</NewPortMappingDescription>" +
							"<NewLeaseDuration>0</NewLeaseDuration>" +
							"</u:AddPortMapping>",
							"AddPortMapping", result.m_serviceName);

						m_peer.LogDebug("Sent UPnP port forward request");
						NetUtility.Sleep(50);
					}
					catch (Exception ex)
					{
						m_peer.LogWarning("UPnP port forward failed: " + ex.Message);
						return false;
					}
				}
			return true;
		}

		/// <summary>
		/// Delete a forwarding rule from the router using UPnP
		/// </summary>
		public bool DeleteForwardingRule(int port)
		{
			if (!CheckAvailability())
				return false;

			lock (DiscoveryResults)
				foreach (var result in DiscoveryResults)
					try
					{
						SOAPRequest(result.m_serviceUrl,
						"<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:" + result.m_serviceName + ":1\">" +
						"<NewRemoteHost>" +
						"</NewRemoteHost>" +
						"<NewExternalPort>" + port + "</NewExternalPort>" +
						"<NewProtocol>" + ProtocolType.Udp.ToString().ToUpper(System.Globalization.CultureInfo.InvariantCulture) + "</NewProtocol>" +
						"</u:DeletePortMapping>", "DeletePortMapping", result.m_serviceName);
					}
					catch (Exception ex)
					{
						m_peer.LogWarning("UPnP delete forwarding rule failed: " + ex.Message);
						return false;
					}

			return true;
		}

		/// <summary>
		/// Retrieve the extern ip using UPnP
		/// </summary>
		public IPAddress GetExternalIP()
		{
			if (!CheckAvailability())
				return null;

			lock (DiscoveryResults)
				foreach (var result in DiscoveryResults)
					try
					{
						var xdoc = SOAPRequest(
									result.m_serviceUrl,
									"<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:" + result.m_serviceName + ":1\">" + "</u:GetExternalIPAddress>",
									"GetExternalIPAddress",
									result.m_serviceName);
						XmlNamespaceManager nsMgr = new XmlNamespaceManager(xdoc.NameTable);
						nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
						string IP = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr).Value;
						return IPAddress.Parse(IP);
					}
					catch (Exception ex)
					{
						m_peer.LogWarning("Failed to get external IP: " + ex.Message);
						return null;
					}
			return null;
		}

		private XmlDocument SOAPRequest(string url, string soap, string function, string servicename)
		{
			string req = "<?xml version=\"1.0\"?>" +
			"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
			"<s:Body>" +
			soap +
			"</s:Body>" +
			"</s:Envelope>";
			WebRequest r = HttpWebRequest.Create(url);
			r.Method = "POST";
			byte[] b = System.Text.Encoding.UTF8.GetBytes(req);
			r.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:" + servicename + ":1#" + function + "\"");
			r.ContentType = "text/xml; charset=\"utf-8\"";
			r.ContentLength = b.Length;
			r.GetRequestStream().Write(b, 0, b.Length);
			using (WebResponse wres = r.GetResponse()) {
				XmlDocument resp = new XmlDocument();
				Stream ress = wres.GetResponseStream();
				resp.Load(ress);
				return resp;
			}
		}
	}
}