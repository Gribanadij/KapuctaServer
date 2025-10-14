using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class ChatServer
{
    static List<TcpClient> clients = new List<TcpClient>();
    static object lockObj = new object();

    static async Task Main(string[] args)
    {
        TcpListener server = new TcpListener(IPAddress.Any, 8888);
        server.Start();
        Console.WriteLine("Сервер запущен на порту 8888...");

        while (true)
        {
            TcpClient client = await server.AcceptTcpClientAsync();
            lock (lockObj)
                clients.Add(client);
            _ = Task.Run(() => HandleClient(client));
        }
    }

    static async Task HandleClient(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[1024];

        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break; // клиент отключился

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine("Получено: " + message);

                // Рассылаем всем, кроме отправителя
                Broadcast(message, client);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ошибка: " + ex.Message);
        }
        finally
        {
            lock (lockObj)
                clients.Remove(client);
            client.Close();
        }
    }

    static void Broadcast(string message, TcpClient sender)
    {
        lock (lockObj)
        {
            foreach (var client in clients)
            {
                if (client != sender && client.Connected)
                {
                    try
                    {
                        byte[] data = Encoding.UTF8.GetBytes(message);
                        client.GetStream().Write(data, 0, data.Length);
                    }
                    catch
                    {
                        // Игнорируем ошибки отправки (клиент мог отключиться)
                    }
                }
            }
        }
    }
}