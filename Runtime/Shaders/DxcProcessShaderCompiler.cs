using System.Diagnostics;
using System.Text;
using GraphData.Compiler.Backends.Hlsl;

namespace GraphData.Compiler.Runtime.Shaders;

/// <summary>
/// Uses an installed dxc.exe to turn the compiler artifact into DXIL.
/// Set DXC_PATH to override the executable lookup for a deployment.
/// </summary>
public sealed class DxcProcessShaderCompiler
{
    public string ExecutablePath { get; }

    public DxcProcessShaderCompiler(string? executablePath = null)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.GetEnvironmentVariable("DXC_PATH") ?? "dxc.exe"
            : executablePath;
    }

    public byte[] Compile(HlslMessagePassingArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(Path.GetTempPath(), $"graphdata-dxc-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "message-passing.hlsl");
        var outputPath = Path.Combine(directory, "message-passing.dxil");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(sourcePath, artifact.Source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var process = Process.Start(CreateStartInfo(artifact, sourcePath, outputPath))
                ?? throw new InvalidOperationException($"Unable to start DXC executable '{ExecutablePath}'.");
            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) => Append(standardOutput, eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Append(standardError, eventArgs.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            });
            process.WaitForExit();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"DXC failed with exit code {process.ExitCode}: {standardError}{Environment.NewLine}{standardOutput}".Trim());
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        static void Append(StringBuilder builder, string? line)
        {
            if (line is null)
                return;
            lock (builder)
                builder.AppendLine(line);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        HlslMessagePassingArtifact artifact,
        string sourcePath,
        string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-T");
        startInfo.ArgumentList.Add(artifact.TargetProfile);
        startInfo.ArgumentList.Add("-E");
        startInfo.ArgumentList.Add(artifact.EntryPoint);
        startInfo.ArgumentList.Add("-HV");
        startInfo.ArgumentList.Add("2021");
        startInfo.ArgumentList.Add("-Qstrip_debug");
        startInfo.ArgumentList.Add("-Qstrip_reflect");
        startInfo.ArgumentList.Add("-Fo");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(sourcePath);
        return startInfo;
    }
}
