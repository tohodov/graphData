using System.Collections.ObjectModel;

namespace GraphData.Compiler.Tensors;

public sealed class DenseTensor
{
    private readonly int[] shape;
    private readonly float[] values;
    private readonly ReadOnlyCollection<int> readOnlyShape;

    public IReadOnlyList<int> Shape => readOnlyShape;
    public int Rank => shape.Length;
    public int ElementCount => values.Length;

    public float this[int index] => values[index];

    public float this[int row, int column]
    {
        get
        {
            if (shape.Length != 2)
                throw new InvalidOperationException("The tensor must have rank 2 for matrix indexing.");
            if ((uint)row >= (uint)shape[0])
                throw new ArgumentOutOfRangeException(nameof(row));
            if ((uint)column >= (uint)shape[1])
                throw new ArgumentOutOfRangeException(nameof(column));
            return values[(row * shape[1]) + column];
        }
    }

    public DenseTensor(IReadOnlyList<int> shape, IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(values);

        this.shape = shape.ToArray();
        if (this.shape.Length == 0)
            throw new ArgumentException("A tensor shape must contain at least one dimension.", nameof(shape));
        if (this.shape.Any(static dimension => dimension < 0))
            throw new ArgumentException("Tensor dimensions cannot be negative.", nameof(shape));

        var expectedLength = this.shape.Aggregate(1, static (product, dimension) => checked(product * dimension));
        this.values = values.ToArray();
        if (this.values.Length != expectedLength)
            throw new ArgumentException(
                $"Tensor shape expects {expectedLength} values, but got {this.values.Length}.",
                nameof(values));

        readOnlyShape = Array.AsReadOnly(this.shape);
    }

    public float[] ToArray() => (float[])values.Clone();
}
