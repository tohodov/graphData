using System;
using System.IO;
using System.Threading.Tasks;
using Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storage;

namespace GraphData.Tests;

internal sealed class TestGraphStorageScope : IAsyncDisposable
{
    private readonly NtfsGraphStorageOptions _options;
}
