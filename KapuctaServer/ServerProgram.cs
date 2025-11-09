using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

public class Message
{
    public char Type { get; set; }
    public byte[] Data { get; set; }
}

public static class MessageParser
{
    public static async Task<Message> ReadMessageAsync(NetworkStream stream)
    {
        var typeByte = await ReadExactlyAsync(stream, 1);
        char type = (char)typeByte[0];

        var lengthBytes = await ReadExactlyAsync(stream, 4);
        int length = BitConverter.ToInt32(lengthBytes, 0);

        if (length < 0 || length > 100_000_000)
            throw new InvalidDataException("Некорректная длина сообщения");

        byte[] data = await ReadExactlyAsync(stream, length);
        return new Message { Type = type, Data = data };
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException("Клиент отключился");
            totalRead += read;
        }
        return buffer;
    }
}

class ServerProgram
{
    private static readonly ConcurrentBag<TcpClient> Clients = new ConcurrentBag<TcpClient>();
    private const int Port = 1337;
    private static readonly string UsersFile = "users.txt";
    private static readonly object FileLock = new object();

    private static readonly string FilesDir = "Files";
    private static int _nextFileId = 1;
    private static readonly object FileIdLock = new object();

    private static int GetNextUserId()
    {
        lock (FileLock)
        {
            if (!File.Exists(UsersFile))
                return 1;

            var users = File.ReadAllLines(UsersFile);
            int maxId = 0;

            foreach (var userLine in users)
            {
                var parts = userLine.Split(new string[] { " | " }, StringSplitOptions.None);
                if (parts.Length >= 3 && int.TryParse(parts[1], out int userId))
                {
                    if (userId > maxId) maxId = userId;
                }
            }

            return maxId + 1;
        }
    }

    static ServerProgram()
    {
        if (!File.Exists(UsersFile))
            File.Create(UsersFile).Close();

        if (!Directory.Exists(FilesDir))
            Directory.CreateDirectory(FilesDir);

        if (Directory.Exists(FilesDir))
        {
            var dirs = Directory.GetDirectories(FilesDir);
            foreach (var dir in dirs)
            {
                if (int.TryParse(Path.GetFileName(dir), out int id) && id >= _nextFileId)
                {
                    _nextFileId = id + 1;
                }
            }
        }
    }

    static async Task Main(string[] args)
    {
        TcpListener listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"Сервер запущен на порту {Port}");

        while (true)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                Console.WriteLine("Новое подключение");
                _ = HandleClientAsync(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при принятии подключения: {ex.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        var stream = client.GetStream();
        UserSession session = null;

        try
        {
            var authMessage = await MessageParser.ReadMessageAsync(stream);
            if (authMessage.Type != 'A')
            {
                Console.WriteLine("Первое сообщение не является аутентификацией");
                client.Close();
                return;
            }

            string authData = Encoding.UTF8.GetString(authMessage.Data);
            string[] parts = authData.Split(new string[] { " | " }, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                Console.WriteLine("Некорректный формат аутентификации");
                client.Close();
                return;
            }

            string password = parts[0].Trim();
            string requestedName = parts[1].Trim();

            if (password.Contains('|') || requestedName.Contains('|') ||
                password.Contains('\n') || requestedName.Contains('\n'))
            {
                Console.WriteLine("Запрещённые символы в пароле или имени");
                client.Close();
                return;
            }

            string userId;
            string finalName;

            lock (FileLock)
            {
                var users = File.ReadAllLines(UsersFile);
                var existing = users.FirstOrDefault(u => u.StartsWith(password + " | "));

                if (existing != null)
                {
                    var userParts = existing.Split(new string[] { " | " }, StringSplitOptions.None);
                    if (userParts.Length >= 3)
                    {
                        userId = userParts[1];
                        finalName = userParts[2];
                    }
                    else
                    {
                        userId = GetNextUserId().ToString("D5");
                        finalName = userParts[1];
                        var updatedUsers = users.Select(u => u == existing ? $"{password} | {userId} | {finalName}" : u).ToArray();
                        File.WriteAllLines(UsersFile, updatedUsers);
                    }
                    Console.WriteLine($"Вход: {finalName} (ID: {userId})");
                }
                else
                {
                    userId = GetNextUserId().ToString("D5");
                    finalName = requestedName;
                    string newRecord = $"{password} | {userId} | {finalName}";
                    File.AppendAllLines(UsersFile, new[] { newRecord });
                    Console.WriteLine($"Регистрация: {finalName} (ID: {userId})");
                }
            }

            string response = $"{userId} | {finalName}";
            await SendMessageAsync(client, 'A', response);

            session = new UserSession { Client = client, UserId = userId, Name = finalName };
            Clients.Add(client);

            while (client.Connected)
            {
                var message = await MessageParser.ReadMessageAsync(stream);

                if (message.Type == 'T')
                {
                    Console.WriteLine($"💬 Сообщение от {session.Name}: {Encoding.UTF8.GetString(message.Data)}");
                    await BroadcastMessageAsync(message, session);
                }
                else if (message.Type == 'F')
                {
                    string fileMetadata = Encoding.UTF8.GetString(message.Data);
                    string[] fileParts = fileMetadata.Split('|');

                    if (fileParts.Length == 2 && long.TryParse(fileParts[1], out long fileSize))
                    {
                        string fileName = fileParts[0];
                        Console.WriteLine($"📥 Файл от {session.Name}: {fileName} ({fileSize} bytes)");

                        int fileId = await SaveFileAsync(stream, fileName, fileSize, session);

                        string fileNotification = $"{session.Name} отправил файл: {fileName} (ID: {fileId})";
                        await BroadcastMessageAsync(new Message
                        {
                            Type = 'F',
                            Data = Encoding.UTF8.GetBytes(fileNotification)
                        }, session);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Клиент отключился: {ex.Message}");
        }
        finally
        {
            if (session != null)
            {
                Clients.TryTake(out _);
            }
            client.Close();
        }
    }

    private static async Task BroadcastMessageAsync(Message message, UserSession sender)
    {
        var clientsToRemove = new List<TcpClient>();

        foreach (var client in Clients)
        {
            if (!client.Connected)
            {
                clientsToRemove.Add(client);
                continue;
            }

            try
            {
                if (message.Type == 'T')
                {
                    string textWithSender = $"{sender.Name}: {Encoding.UTF8.GetString(message.Data)}";
                    await SendMessageAsync(client, 'T', textWithSender);
                }
                else if (message.Type == 'F')
                {
                    string fileWithSender = $"{sender.Name} отправил файл: {Encoding.UTF8.GetString(message.Data)}";
                    await SendMessageAsync(client, 'F', fileWithSender);
                }
            }
            catch
            {
                clientsToRemove.Add(client);
            }
        }

        foreach (var dead in clientsToRemove)
            Clients.TryTake(out _);
    }

    private static async Task SendMessageAsync(TcpClient client, char type, string data)
    {
        var stream = client.GetStream();
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        await stream.WriteAsync(new byte[] { (byte)type }, 0, 1);
        await stream.WriteAsync(BitConverter.GetBytes(dataBytes.Length), 0, 4);
        await stream.WriteAsync(dataBytes, 0, dataBytes.Length);
        await stream.FlushAsync();
    }

    private static async Task<int> SaveFileAsync(NetworkStream stream, string fileName, long fileSize, UserSession sender)
    {
        int fileId;
        string fileDir, filePath;

        lock (FileIdLock)
        {
            fileId = _nextFileId++;
            fileDir = Path.Combine(FilesDir, fileId.ToString());
            Directory.CreateDirectory(fileDir);
            filePath = Path.Combine(fileDir, fileName);
        }

        Console.WriteLine($"💾 Сохранение файла: {fileName} ({fileSize} bytes) как ID {fileId}");

        using (var fileStream = File.Create(filePath))
        {
            byte[] buffer = new byte[64 * 1024];
            long totalRead = 0;

            while (totalRead < fileSize)
            {
                int toRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                    throw new EndOfStreamException("Соединение прервано при передаче файла");

                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                double progress = (double)totalRead / fileSize * 100;
                Console.Write($"\r💾 Прогресс: [{GetProgressBar(progress)}] {progress:F1}%");
            }
            Console.WriteLine();
        }

        return fileId;
    }

    private static string GetProgressBar(double percentage)
    {
        int width = 20;
        int progressWidth = (int)(percentage / 100 * width);
        return new string('#', progressWidth) + new string('-', width - progressWidth);
    }
}

class UserSession
{
    public TcpClient Client;
    public string UserId;
    public string Name;
}