// The MIT License (MIT)
//
// Copyright (c) 2015 Microsoft
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace MetricSystem.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using MetricSystem.Data;
    using MetricSystem.Utilities;

    internal sealed class RegistrationClient : IDisposable
    {
        public const string RegistrationEndpoint = "/register";
        public const string RegistrationHostnameParameter = "hostname";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly string destinationHostname;
        private readonly ushort destinationPort;
        private readonly string sourceHostname;
        private readonly string sourceMachineFunction;
        private readonly string sourceDatacenter;
        private readonly ushort sourcePort;
        private readonly TimeSpan registrationInterval;
        private readonly DataManager dataManager;

        private readonly HttpClient httpClient;
        private Timer registrationTimer;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        public RegistrationClient(string destinationHostname, ushort destinationPort, string sourceHostname,
                                  ushort sourcePort, string sourceMachineFunction, string sourceDatacenter,
                                  TimeSpan registrationInterval, DataManager dataManager)
        {
            if (Uri.CheckHostName(destinationHostname) == UriHostNameType.Unknown)
            {
                throw new ArgumentException("Invalid hostname.", "destinationHostname");
            }
            if (destinationPort == 0)
            {
                throw new ArgumentOutOfRangeException("destinationPort");
            }

            if (Uri.CheckHostName(sourceHostname) == UriHostNameType.Unknown)
            {
                throw new ArgumentException("Invalid hostname.", "sourceHostname");
            }
            if (sourcePort == 0)
            {
                throw new ArgumentOutOfRangeException("sourcePort");
            }

            if (registrationInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("registrationInterval");
            }

            if (dataManager == null)
            {
                throw new ArgumentNullException("dataManager");
            }

            this.destinationHostname = destinationHostname;
            this.destinationPort = destinationPort;
            this.sourceHostname = sourceHostname;
            this.sourceMachineFunction = sourceMachineFunction ?? string.Empty;
            this.sourceDatacenter = sourceDatacenter ?? string.Empty;
            this.sourcePort = sourcePort;
            this.registrationInterval = registrationInterval > RequestTimeout ? registrationInterval : RequestTimeout;
            this.dataManager = dataManager;

            this.httpClient = new HttpClient(
                new WebRequestHandler
                {
                    AllowAutoRedirect = false,
                    AllowPipelining = true,
                    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
                });
            this.httpClient.Timeout = RequestTimeout;
        }

        public void Dispose()
        {
            lock (this)
            {
                if (this.registrationTimer != null)
                {
                    this.registrationTimer.Dispose();
                    this.registrationTimer = null;

                    // We don't want to null httpClient because an async task may be trying to use it. The task will
                    // properly handle both OperationCanceled and ObjectDisposed exceptions for us.
                    this.httpClient.CancelPendingRequests();
                    this.httpClient.Dispose();
                }
            }
        }

        public void Start()
        {
            lock (this)
            {
                if (this.registrationTimer != null)
                {
                    throw new InvalidOperationException("Cannot start more than once.");
                }

                this.registrationTimer = new Timer(_ => this.StartRegistration(), null, TimeSpan.Zero, TimeSpan.Zero);
            }
        }

        private async void StartRegistration()
        {
            IPAddress[] addresses;
            try
            {
                addresses =
                    await Task<IPAddress[]>.Factory.FromAsync(Dns.BeginGetHostAddresses, Dns.EndGetHostAddresses,
                                                              this.destinationHostname, null);
            }
            catch (SocketException e)
            {
                Events.Write.RegistrationDestinationResolutionFailed(this.destinationHostname, e.Message);
                return;
            }

            var serverRegistration = new ServerRegistration
                                     {
                                         Hostname = this.sourceHostname,
                                         Port = this.sourcePort,
                                         MachineFunction = this.sourceMachineFunction,
                                         Datacenter = this.sourceDatacenter,
                                     };

            foreach (var counter in this.dataManager.Counters)
            {
                serverRegistration.Counters.Add(
                                                new CounterInfo
                                                {
                                                    Name = counter.Name,
                                                    Type = counter.Type,
                                                    Dimensions = counter.Dimensions.ToList(),
                                                    StartTime = counter.StartTime.ToMillisecondTimestamp(),
                                                    EndTime = counter.EndTime.ToMillisecondTimestamp(),
                                                });
            }

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var writerStream = new WriterStream(ms, this.dataManager.MemoryStreamManager))
            {
                var writer = writerStream.CreateCompactBinaryWriter();
                writer.Write(serverRegistration);
                payload = ms.ToArray();
            }

            foreach (var address in addresses)
            {
                this.RegisterWithAddress(address, payload);
            }

            lock (this)
            {
                if (this.registrationTimer != null)
                {
                    this.registrationTimer.Change(this.registrationInterval, TimeSpan.Zero);
                }
            }
        }

        private async void RegisterWithAddress(IPAddress address, byte[] payload)
        {
            string uriHostname = address.ToString();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                uriHostname = '[' + uriHostname + ']'; // required because of ':' chars in IPv6 addresses
            }

            var uri =
                new UriBuilder(Uri.UriSchemeHttp, uriHostname, this.destinationPort, RegistrationEndpoint).ToString();

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, uri) {Content = new ByteArrayContent(payload)};
                request.Headers.Add("Connection", "Keep-Alive");

                var response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    Events.Write.RegistrationSucceeded(uri);
                }
                else
                {
                    Events.Write.RegistrationFailed(uri, (int)response.StatusCode, response.ReasonPhrase);
                }
            }
            catch (HttpRequestException e)
            {
                Events.Write.RegistrationFailed(uri, -1, e.Message);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
