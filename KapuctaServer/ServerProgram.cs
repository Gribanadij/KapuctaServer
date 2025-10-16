using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class ServerProgram
{
    static List<StreamWriter> writers = new List<StreamWriter>();
    static readonly string accountsDir = Path.Combine(Directory.GetCurrentDirectory(), "accounts");
    static readonly string chatsDir = Path.Combine(Directory.GetCurrentDirectory(), "chats");
    static readonly string usersFile = Path.Combine(accountsDir, "users.txt");
    static readonly string chatLogFile = Path.Combine(chatsDir, "public_chat.log");

    static void Main(string[] args)
    {
        Directory.CreateDirectory(accountsDir);
        Directory.CreateDirectory(chatsDir);
        if (!File.Exists(usersFile)) File.Create(usersFile).Close();

        TcpListener server = new TcpListener(IPAddress.Any, 8888);
        server.Start();
        Console.WriteLine("Сервер запущен на порту 8888...");

        while (true)
        {
            TcpClient client = server.AcceptTcpClient();
            _ = HandleClientAsync(client); // запускаем обработку клиента
        }
    }

    static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        StreamReader reader = new StreamReader(stream);
        StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };

        try
        {
            // === ИСПРАВЛЕННЫЙ ПАРСИНГ AUTH ===
            string authLine = await reader.ReadLineAsync();
            if (authLine == null || !authLine.StartsWith("AUTH:"))
            {
                await writer.WriteLineAsync("ERROR: Ожидалась аутентификация");
                return;
            }

            // Разбиваем "AUTH:password|name" → ["AUTH", "password|name"]
            string[] authParts = authLine.Split(new char[] { ':' }, 2, StringSplitOptions.None);
            if (authParts.Length != 2)
            {
                await writer.WriteLineAsync("ERROR: Неверный формат AUTH");
                return;
            }

            // Разбиваем "password|name" → ["password", "name"]
            string[] userParts = authParts[1].Split(new char[] { '|' }, 2, StringSplitOptions.None);
            if (userParts.Length != 2)
            {
                await writer.WriteLineAsync("ERROR: Неверный формат данных");
                return;
            }

            string password = userParts[0];
            string name = userParts[1];
            // ===================================

            // Проверяем или добавляем аккаунт
            bool userExists = false;
            if (File.Exists(usersFile))
            {
                foreach (string line in File.ReadAllLines(usersFile))
                {
                    if (line.StartsWith(password + "|"))
                    {
                        userExists = true;
                        break;
                    }
                }
            }

            if (!userExists)
            {
                File.AppendAllText(usersFile, $"{password}|{name}{Environment.NewLine}");
                Console.WriteLine($"Новый аккаунт: {name} ({password})");
            }

            await writer.WriteLineAsync("OK");

            // Добавляем в список рассылки
            lock (writers)
            {
                writers.Add(writer);
            }

            // Отправляем историю чата
            if (File.Exists(chatLogFile))
            {
                foreach (string line in File.ReadAllLines(chatLogFile))
                {
                    await writer.WriteLineAsync(line);
                }
            }

            // Основной цикл приёма сообщений
            while (true)
            {
                string message = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(message)) break;

                // Сохраняем в лог
                File.AppendAllText(chatLogFile, message + Environment.NewLine);

                // Рассылаем всем (без await внутри lock!)
                StreamWriter[] currentWriters;
                lock (writers)
                {
                    currentWriters = writers.ToArray();
                }

                var tasks = new List<Task>();
                foreach (var w in currentWriters)
                {
                    if (w.BaseStream.CanWrite)
                    {
                        tasks.Add(w.WriteLineAsync(message));
                    }
                }
                await Task.WhenAll(tasks);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка клиента: {ex.Message}");
        }
        finally
        {
            // Удаляем из рассылки
            lock (writers)
            {
                writers.Remove(writer);
            }

            // Закрываем ресурсы
            try { writer?.Close(); } catch { }
            try { reader?.Close(); } catch { }
            try { client?.Close(); } catch { }
        }
    }
}