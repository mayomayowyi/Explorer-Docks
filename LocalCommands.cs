using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerDocks
{
    internal static class LocalCommands
    {
        private const string Name = "ExplorerDocks.Commands.v1";

        public static void Send(string[] args)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
                pipe.Connect(1200);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(args));
            }
            catch { /* 常駐アプリが起動中でない場合は無視 */ }
        }

        public static async Task ListenAsync(Action<string[]> onMessage, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop);
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(stop);
                    if (message is null || message.Length > 8192) continue;
                    var args = JsonSerializer.Deserialize<string[]>(message);
                    if (args is not null) onMessage(args);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
                catch (Exception)
                {
                    if (stop.IsCancellationRequested) break;
                    try { await Task.Delay(500, stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }
}
