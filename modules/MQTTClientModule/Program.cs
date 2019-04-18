namespace MQTTClientModule
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Microsoft.Extensions.Logging;
    using Microsoft.Azure.Devices;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Azure.Devices.Client;
    using Newtonsoft.Json.Linq;
    using System.Security.Cryptography.X509Certificates;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    // related to MQTTnet
    using MQTTnet;
    using MQTTnet.Client;
    using MQTTnet.Protocol;

    class Program
    {
        static int Temp_Threshold { get; set; } = 25;
        public static string MQTT_BROKER_ADDRESS = "192.168.43.134";
        public static int MQTT_BROKER_PORT = 4321;
        public static IMqttClient MqttClient { get; set; } = null;

        public static int NBDevices = 0;
        public static List<DeviceConfig> Devices { get; set; }

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            Console.WriteLine("In Init()");

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Read the TemperatureThreshold value from the module twin's desired properties
            var moduleTwin = await ioTHubModuleClient.GetTwinAsync();
            await OnDesiredPropertiesUpdate(moduleTwin.Properties.Desired, ioTHubModuleClient);

            // Attach a callback for updates to the module twin's desired properties.
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdate, null);
        }

        static Task OnDesiredPropertiesUpdate(TwinCollection desiredProperties, object userContext)
        {
            try
            {
                Console.WriteLine($"{DateTime.Now.ToString()} - Desired property change:");
                Console.WriteLine(JsonConvert.SerializeObject(desiredProperties));
           
                if (desiredProperties.Contains("Temp_Threshold") && desiredProperties["Temp_Threshold"]!=null)
                {
                    if(int.TryParse(desiredProperties["Temp_Threshold"].ToString(), out int ts))
                    {
                        Temp_Threshold = ts;
                        Console.WriteLine($"value updated for Property 'Temp_Threshold': {Temp_Threshold}");
                    }
                    else
                    {
                        Console.WriteLine($"Check the property Temp_Threshold ({desiredProperties["Temp_Threshold"]}) in the Module Twin. It must be an int");
                    }
                }

                if (desiredProperties.Contains("MQTT_BROKER_ADDRESS") && desiredProperties["MQTT_BROKER_ADDRESS"]!=null)
                {
                    MQTT_BROKER_ADDRESS = desiredProperties["MQTT_BROKER_ADDRESS"];
                    Console.WriteLine($"value updated for Property 'MQTT_BROKER_ADDRESS': {MQTT_BROKER_ADDRESS}");
                }

                if (desiredProperties.Contains("MQTT_BROKER_PORT") && desiredProperties["MQTT_BROKER_PORT"]!=null)
                {
                    if(int.TryParse(desiredProperties["MQTT_BROKER_PORT"].ToString(), out int port))
                    {
                        MQTT_BROKER_PORT = port;
                        Console.WriteLine($"value updated for Property 'MQTT_BROKER_PORT': {MQTT_BROKER_PORT}");
                    }
                    else
                    {
                        Console.WriteLine($"Check the property MQTT_BROKER_PORT ({desiredProperties["MQTT_BROKER_PORT"]}) in the Module Twin. It must be an int");
                    }
                }

                if (desiredProperties.Contains("NBDevices") && desiredProperties["NBDevices"]!=null)
                {
                    if(int.TryParse(desiredProperties["NBDevices"].ToString(), out int nb))
                    {
                        NBDevices = nb;
                        Console.WriteLine($"value updated for Property 'NBDevices': {NBDevices}");
                    }
                    else
                    {
                        Console.WriteLine($"Check the property NBDevices ({desiredProperties["NBDevices"]}) in the Module Twin. It must be an int");
                    }
                }

                Devices = new List<DeviceConfig>();
                for (int i=1; i<=NBDevices; i++)
                {
                    string deviceIdKey = $"Device{i}_ID";
                    string deviceSchemaKey = $"Device{i}_Schema";
                    string deviceDataTopicKey = $"Device{i}_DataTopic";
                    string deviceFeedbackTopicKey = $"Device{i}_FeedbackTopic";
                    
                    DeviceConfig deviceConfig = new DeviceConfig();

                    if (desiredProperties.Contains(deviceIdKey) && desiredProperties[deviceIdKey]!=null)
                    {
                        deviceConfig.ID = desiredProperties[deviceIdKey];
                    }
                    if (desiredProperties.Contains(deviceSchemaKey) && desiredProperties[deviceSchemaKey]!=null)
                    {
                        deviceConfig.Schema = desiredProperties[deviceSchemaKey];
                    }
                    if (desiredProperties.Contains(deviceDataTopicKey) && desiredProperties[deviceDataTopicKey]!=null)
                    {
                        deviceConfig.DataTopic = desiredProperties[deviceDataTopicKey];
                    }
                    if (desiredProperties.Contains(deviceFeedbackTopicKey) && desiredProperties[deviceFeedbackTopicKey]!=null)
                    {
                        deviceConfig.FeedbackTopic = desiredProperties[deviceFeedbackTopicKey];
                    }
                    Devices.Add(deviceConfig);
                    Console.WriteLine($"Device added: {deviceConfig.ID}");                    
                }
                
                Console.WriteLine("Subscribing to Topics");
                Task.Run(SubscribeMQTTTopicsAsync).Wait();
            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine("Error when receiving desired property: {0}", exception);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Error when receiving desired property: {0}", ex.Message);
            }
            return Task.CompletedTask;
        }

        public static async Task<IMqttClient> ConnectAsync(string clientId)
        {
            var client = new MqttFactory().CreateMqttClient();
            X509Certificate ca_crt = new X509Certificate("certs/ca.crt");

            var tlsOptions = new MqttClientOptionsBuilderTlsParameters();
            tlsOptions.SslProtocol = System.Security.Authentication.SslProtocols.Tls12;
            tlsOptions.Certificates = new List<IEnumerable<byte>>() { ca_crt.Export(X509ContentType.Cert).Cast<byte>() };
            tlsOptions.UseTls = true;
            tlsOptions.AllowUntrustedCertificates = true;
            tlsOptions.IgnoreCertificateChainErrors = false;
            tlsOptions.IgnoreCertificateRevocationErrors = false;

            var options = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(MQTT_BROKER_ADDRESS, MQTT_BROKER_PORT)
            .WithTls(tlsOptions)
            .Build();

            await client.ConnectAsync(options);

            return client;
        }

        public static async Task SubscribeMQTTTopicsAsync()
        {
            MqttClient = await ConnectAsync("IoTEdgeModule");
            MqttClient.ApplicationMessageReceived += async (sender, eventArgs) => { await Client1_ApplicationMessageReceived(sender, eventArgs); };

            foreach(DeviceConfig deviceConfig in Devices)
            {
                // await MqttClient.SubscribeAsync("/Test1/temperature",MqttQualityOfServiceLevel.ExactlyOnce);
                await MqttClient.SubscribeAsync(deviceConfig.DataTopic, MqttQualityOfServiceLevel.ExactlyOnce);
                Console.WriteLine($"Subscribed to Topic: {deviceConfig.DataTopic}");
            }
        }

        public static async Task PublishMQTTMessageAsync(DeviceConfig deviceConfig, string payload)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(deviceConfig.FeedbackTopic)
                .WithPayload(payload)
                .WithExactlyOnceQoS()
                .WithRetainFlag()
                .Build();

            if (MqttClient == null | !MqttClient.IsConnected)
            {
                MqttClient = await ConnectAsync("IoTEdgeModule");
            }

            await MqttClient.PublishAsync(message);
            Console.WriteLine($"Message '{payload}' sent to {deviceConfig.FeedbackTopic}");
        }


        private static async Task Client1_ApplicationMessageReceived(object sender, MqttApplicationMessageReceivedEventArgs eventArgs)
        {
            var info = $"Timestamp: {DateTime.Now:O} | Topic: {eventArgs.ApplicationMessage.Topic} | Payload: {Encoding.UTF8.GetString(eventArgs.ApplicationMessage.Payload)} | QoS: {eventArgs.ApplicationMessage.QualityOfServiceLevel}";
            Console.WriteLine($"Message: {info}");

            var payload = Encoding.UTF8.GetString(eventArgs.ApplicationMessage.Payload);
            
            try
            {
                string topic = eventArgs.ApplicationMessage.Topic;
                DeviceConfig deviceConfig = Devices.Find(d => d.DataTopic.Equals(topic));

                string dataBuffer = "Unkown format";
                if (deviceConfig.Schema.Equals("DefaultEngine"))
                {
                    var messageBody = JsonConvert.DeserializeObject<MessageBody>(payload);

                    if (messageBody.Machine.Temperature >= Temp_Threshold)
                    {
                        Console.WriteLine("Alert: over Temp_Threshold");
                        await PublishMQTTMessageAsync(deviceConfig, messageBody.TimeCreated.ToLongTimeString());
                    }

                    dataBuffer = JsonConvert.SerializeObject(messageBody);
                }

                var message = new Message(Encoding.UTF8.GetBytes(dataBuffer));

                //TODO: package sous forme de propriété
                MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
                ITransportSettings[] settings = { mqttSetting };
                ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
                await ioTHubModuleClient.OpenAsync();
                await ioTHubModuleClient.SendEventAsync("output1", message);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    class MessageBody
    {
        public Machine Machine {get;set;}
        public Ambient Ambient {get; set;}
        public DateTime TimeCreated {get; set;}
    }
    class Machine
    {
        public string Id {get; set;}
        public double Temperature {get; set;}
        public double Pressure {get; set;}         
    }
    class Ambient
    {
        public double Temperature {get; set;}
        public int Humidity {get; set;}         
    }

    class DeviceConfig
    {
        public string ID {get; set;}
        public String Schema {get; set;}     
        public String DataTopic {get; set;}     
        public String FeedbackTopic {get; set;}         
    }
}