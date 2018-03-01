using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace psub
{
  class Program
  {
    static void Debug(string s)
    {
#if DEBUG
      Console.WriteLine(s);
#endif
    }

    static void Main(string[] args)
    {
      var pipeName = Guid.NewGuid().ToString();
      Console.WriteLine($@"\\.\pipe\{pipeName}");

      var cpExe = args[0];
      var cpArgs = string.Join(" ", args.Skip(1).ToArray());
      var psi = new ProcessStartInfo()
      {
        FileName = cpExe,
        Arguments = cpArgs,

        UseShellExecute = false,
        RedirectStandardOutput = true
      };

      // Start the process
      using (var mem = new MemoryStream())
      using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
      {
        process.Start();
        Debug("Process started");

        var copyOutput = process.StandardOutput.BaseStream.CopyToAsync(mem);

        // Spawn pipe servers for any incoming connections

        var red = 5;

        // Cancel waiting for connections when at least one pipe has been fed the full stream
        var cts = new CancellationTokenSource();
        var pipesFed = Enumerable.Repeat(0, red).Select(_ => Task.Run(async () =>
        {
          Debug("Starting pipe server");

          using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, red, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
          {
            Debug("Waiting for connection");
            try
            {
              await pipe.WaitForConnectionAsync(cts.Token);
              Debug("Received connection");
            }
            catch (OperationCanceledException)
            {
              return;
            }

            await copyOutput;
            Debug("Finished waiting for output copying");

            var buffer = mem.ToArray();
            Debug("Retrieved buffer");
            Debug($"Buffer size is {buffer.Length}");

            using (var feeder = new MemoryStream(buffer))
            {
              try
              {
                feeder.CopyTo(pipe);
                Debug("Emitted all data to pipe");
              }
              catch (IOException ex) when (ex.Message == "Pipe is broken.")
              {
                return;
              }
            }

            // Stop waiting for new connections once all output is available
            cts.Cancel();
            Debug("Abort waiting for new connections");
          }
        })).ToList();

        process.WaitForExit();
        Debug("Process exited");

        Task.WhenAll(pipesFed).Wait();
        Debug("All pipe servers finished");
      }
    }
  }
}
