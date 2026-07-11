using System.Collections.ObjectModel;

namespace GraphData.Compiler.Tensors;

public sealed class CsrMatrix
{
    private readonly int[] rowOffsets;
    private readonly int[] columnIndices;
    private readonly float[] values;

    public int RowCount { get; }
    public int ColumnCount { get; }
    public int NonZeroCount => values.Length;
    public IReadOnlyList<int> RowOffsets { get; }
    public IReadOnlyList<int> ColumnIndices { get; }
    public IReadOnlyList<float> Values { get; }

    public CsrMatrix(
        int rowCount,
        int columnCount,
        IEnumerable<int> rowOffsets,
        IEnumerable<int> columnIndices,
        IEnumerable<float> values)
    {
        if (rowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (columnCount < 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        ArgumentNullException.ThrowIfNull(rowOffsets);
        ArgumentNullException.ThrowIfNull(columnIndices);
        ArgumentNullException.ThrowIfNull(values);

        RowCount = rowCount;
        ColumnCount = columnCount;
        this.rowOffsets = rowOffsets.ToArray();
        this.columnIndices = columnIndices.ToArray();
        this.values = values.ToArray();

        Validate();

        RowOffsets = Array.AsReadOnly(this.rowOffsets);
        ColumnIndices = Array.AsReadOnly(this.columnIndices);
        Values = Array.AsReadOnly(this.values);
    }

    public int[] CopyRowOffsets() => (int[])rowOffsets.Clone();
    public int[] CopyColumnIndices() => (int[])columnIndices.Clone();
    public float[] CopyValues() => (float[])values.Clone();

    private void Validate()
    {
        if (rowOffsets.Length != RowCount + 1)
            throw new ArgumentException("CSR row offsets must contain RowCount + 1 elements.", nameof(rowOffsets));
        if (columnIndices.Length != values.Length)
            throw new ArgumentException("CSR column indices and values must have equal lengths.");
        if (rowOffsets[0] != 0 || rowOffsets[^1] != values.Length)
            throw new ArgumentException("CSR row offsets must start at zero and end at the non-zero count.", nameof(rowOffsets));

        for (var row = 0; row < RowCount; row++)
        {
            var start = rowOffsets[row];
            var end = rowOffsets[row + 1];
            if (start > end || start < 0 || end > values.Length)
                throw new ArgumentException("CSR row offsets must be monotone and within the value array.", nameof(rowOffsets));

            var previousColumn = -1;
            for (var index = start; index < end; index++)
            {
                var column = columnIndices[index];
                if ((uint)column >= (uint)ColumnCount)
                    throw new ArgumentException("A CSR column index is outside the matrix bounds.", nameof(columnIndices));
                if (column < previousColumn)
                    throw new ArgumentException("CSR column indices must be sorted inside each row.", nameof(columnIndices));
                previousColumn = column;
            }
        }
    }
}
