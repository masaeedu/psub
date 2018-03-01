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

        // Windows tools like type will often open a pipe several times
        // We need to be prepared to serve the output to an arbitrary number
        // of clients

        // We can stop waiting for more clients once at least one client
        // has been fed the whole output
        var redundancy = 5;

        // Cancel waiting for connections when at least one pipe has been fed the full stream
        var cts = new CancellationTokenSource();
        var pipesFed = Enumerable.Repeat(0, redundancy).Select(_ => Task.Run(async () =>
        {
          Debug("Starting pipe server");

          using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, redundancy, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
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
            Debug("Finished waiting for output");

            var buffer = mem.ToArray();
            Debug("Retrieved output buffer");
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

            // Stop waiting for new connections once at least one pipe is fed
            cts.Cancel();
            Debug("Aborted waiting for further connections");
          }
        })).ToList();

        process.WaitForExit();
        Debug("Child process exited");

        Task.WhenAll(pipesFed).Wait();
        Debug("Output fed to all connected pipes");
      }
    }
  }
}
