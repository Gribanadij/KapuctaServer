using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace KapuctaServer
{
    public class Message
    {
        public char Type { get; set; }
        public byte[] Data { get; set; }

        public string Text => Encoding.UTF8.GetString(Data);
    }

    public static class MessageParser
    {
        public static async Task<Message> ReadMessageAsync(NetworkStream stream)
        {
            // Читаем 1 байт — тип
            var typeByte = await ReadExactlyAsync(stream, 1);
            char type = (char)typeByte[0];

            // Читаем 4 байта — длина данных
            var lengthBytes = await ReadExactlyAsync(stream, 4);
            // .NET использует little-endian по умолчанию
            int length = BitConverter.ToInt32(lengthBytes, 0);

            if (length < 0 || length > 100_000_000) // Защита от спама/ошибок
                throw new InvalidDataException("Некорректная длина сообщения");

            // Читаем данные
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
}
