using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Выберите режим: 1 - Сервер, 2 - Клиент");
            string mode = Console.ReadLine();

            Console.Write("Введите IP адрес сервера: ");
            string serverIp = Console.ReadLine();

            Console.Write("Введите порт: ");
            int port = int.Parse(Console.ReadLine());

            if (mode == "1")
            {
                var server = new ChatServer();
                await server.Start(serverIp, port);
            }
            else if (mode == "2")
            {
                var client = new ChatClient();
                await client.Connect(serverIp, port);
            }
            else
            {
                Console.WriteLine("Неверный режим");
            }
        }
    }

    class ChatServer
    {
        private TcpListener _tcpListener;
        private UdpClient _udpListener;
        private readonly List<TcpClient> _tcpClients = new List<TcpClient>();
        private readonly List<IPEndPoint> _udpEndPoints = new List<IPEndPoint>();
        private readonly object _lock = new object();
        public const string CounterFile = "client_counter.txt";
        public async Task Start(string ip, int port)
        {
            try
            {
                // Сбрасываем счетчик клиентов при запуске сервера
                ResetClientCounter();

                _tcpListener = new TcpListener(IPAddress.Parse(ip), port);
                _tcpListener.Start();
                _udpListener = new UdpClient(port);

                Console.WriteLine($"Сервер запущен на {ip}:{port}");

                await Task.WhenAll(AcceptTcpClients(), AcceptUdpClients());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }

        private void ResetClientCounter()
        {
            try
            {
                if (File.Exists(CounterFile))
                {
                    File.WriteAllText(CounterFile, "2");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось сбросить счетчик клиентов: {ex.Message}");
            }
        }

        private async Task AcceptTcpClients()
        {
            while (true)
            {
                var client = await _tcpListener.AcceptTcpClientAsync();
                lock (_lock) _tcpClients.Add(client);
                _ = HandleTcpClient(client);
            }
        }

        private async Task HandleTcpClient(TcpClient client)
        {
            string clientInfo = null;
            string _userName = null;
            try
            {
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        var ipEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                        clientInfo = $"{ipEndPoint.Address}:{ipEndPoint.Port}";

                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        var parts = message.Split(new[] { ":NAME:" }, StringSplitOptions.None);

                        if (parts.Length == 2 && parts[0].StartsWith("UDP_PORT:"))
                        {
                            int udpPort = int.Parse(parts[0].Substring(9));
                            _userName = parts[1];

                            lock (_lock) _udpEndPoints.Add(new IPEndPoint(ipEndPoint.Address, udpPort));

                            string connectMessage = $"[Сервер] {clientInfo} ({_userName}) подключился";
                            Console.WriteLine(connectMessage);
                            await BroadcastUdp(connectMessage);
                        }
                    }

                    while (true)
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        Console.WriteLine($"Получено (TCP): {msg}");
                        await BroadcastTcp(msg, client);
                    }
                }
            }
            finally
            {
                if (clientInfo != null)
                {
                    string disconnectMessage = $"[Сервер] {clientInfo} отключился";
                    Console.WriteLine(disconnectMessage);
                    await BroadcastUdp(disconnectMessage);

                    lock (_lock)
                    {
                        _tcpClients.Remove(client);
                        var ipEndPoint = (IPEndPoint)client.Client.RemoteEndPoint;
                        _udpEndPoints.RemoveAll(ep =>
                            ep.Address.Equals(ipEndPoint.Address) &&
                            ep.Port == ipEndPoint.Port);
                    }
                }
                client.Dispose();
            }
        }

        private async Task AcceptUdpClients()
        {
            while (true) await _udpListener.ReceiveAsync();
        }

        private async Task BroadcastTcp(string message, TcpClient sender)
        {
            var data = Encoding.UTF8.GetBytes(message);
            List<TcpClient> clientsCopy;
            lock (_lock) clientsCopy = new List<TcpClient>(_tcpClients);

            foreach (var client in clientsCopy)
            {
                if (client != sender && client.Connected)
                {
                    try
                    {
                        await client.GetStream().WriteAsync(data, 0, data.Length);
                    }
                    catch
                    {
                        lock (_lock) _tcpClients.Remove(client);
                    }
                }
            }
        }

        private async Task BroadcastUdp(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            List<IPEndPoint> endpointsCopy;
            lock (_lock) endpointsCopy = new List<IPEndPoint>(_udpEndPoints);

            foreach (var endpoint in endpointsCopy)
            {
                await _udpListener.SendAsync(data, data.Length, endpoint);
            }
        }
    }

    class ChatClient
    {
        private static readonly string CounterFile = ChatServer.CounterFile;
        private TcpClient _tcpClient;
        private UdpClient _udpClient;
        private int _udpPort;
        private string _userName;
        private string _clientIp;

        public async Task Connect(string serverIp, int port)
        {
            try
            {
                // Получаем следующий IP-адрес из файла
                _clientIp = GetNextClientIp();
                Console.WriteLine($"Клиент использует IP: {_clientIp}");

                // Устанавливаем TCP соединение 
                _tcpClient = new TcpClient(new IPEndPoint(IPAddress.Parse(_clientIp), 0));
                await _tcpClient.ConnectAsync(IPAddress.Parse(serverIp), port);

                // Ждем завершения handshake перед отправкой данных
                await Task.Delay(100);

                // Создаем UDP клиент только после установки TCP соединения
                _udpClient = new UdpClient(new IPEndPoint(IPAddress.Parse(_clientIp), 0));
                _udpPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;

                // Получаем поток только после handshake
                var stream = _tcpClient.GetStream();

                Console.Write("Введите ваше имя: ");
                _userName = Console.ReadLine();

                string combinedData = $"UDP_PORT:{_udpPort}:NAME:{_userName}";
                byte[] data = Encoding.UTF8.GetBytes(combinedData);
                await stream.WriteAsync(data, 0, data.Length);

                await Task.WhenAny(
                    ReceiveTcpMessages(),
                    ReceiveUdpNotifications(),
                    SendMessages()
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }

        private string GetNextClientIp()
        {
            int counter = 2;

            try
            {
                if (File.Exists(CounterFile))
                {
                    string content = File.ReadAllText(CounterFile);
                    if (int.TryParse(content, out int savedCounter))
                    {
                        counter = savedCounter;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка чтения счетчика: {ex.Message}");
            }

            string ip = $"127.0.0.{counter}";

            counter++;

            try
            {
                File.WriteAllText(CounterFile, counter.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка записи счетчика: {ex.Message}");
            }

            return ip;
        }

        private async Task ReceiveTcpMessages()
        {
            var stream = _tcpClient.GetStream();
            var buffer = new byte[1024];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
        }

        private async Task ReceiveUdpNotifications()
        {
            while (true)
            {
                var result = await _udpClient.ReceiveAsync();
                Console.WriteLine($"[Уведомление] {Encoding.UTF8.GetString(result.Buffer)}");
            }
        }

        private async Task SendMessages()
        {
            var stream = _tcpClient.GetStream();
            while (true)
            {
                string input = await Task.Run(() => Console.ReadLine());
                byte[] data = Encoding.UTF8.GetBytes($"{_userName}: {input}");
                await stream.WriteAsync(data, 0, data.Length);
            }
        }
    }
}