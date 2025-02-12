#nullable enable
#pragma warning disable CA1069
using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace ExampleNamespace;
public static unsafe partial class ExampleClass
{
    public partial class BindgenInternal
    {
        public const string DllImportPath = @"libexample";
    }

    [DllImport(BindgenInternal.DllImportPath, EntryPoint = "example_function")]
    public static extern byte example_function(example_struct_t example_parameter);

    public enum example_enum_t : uint
    {
        red = 0,
        green = 1,
        blue = 2
    }

    public const example_enum_t red = example_enum_t.red;

    public const example_enum_t green = example_enum_t.green;

    public const example_enum_t blue = example_enum_t.blue;

    public partial struct example_struct_t
    {
        public uint integer;

        public InlineArrays.uint_4 array;
    }

    public partial struct InlineArrays
    {
        [InlineArray(4)]
        public partial struct uint_4
        {
            public uint Item0;
        }
    }

    public const int five = 5;

    public const string hello_world = "Hello World";

    public const int ten = 10;

    public const string world = "World";

    public partial struct example_struct_t : IEquatable<example_struct_t>
    {
        public bool Equals(example_struct_t other)
        {
            fixed (example_struct_t* __self = &this)
            {
                return new Span<byte>(__self, sizeof(example_struct_t)).SequenceEqual(new Span<byte>(&other, sizeof(example_struct_t)));
            }
        }

        public override bool Equals(object? obj)
        {
            return obj is example_struct_t other && Equals(other);
        }

        public static bool operator ==(example_struct_t left, example_struct_t right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(example_struct_t left, example_struct_t right)
        {
            return !(left == right);
        }

        public override int GetHashCode()
        {
            fixed (example_struct_t* __self = &this)
            {
                HashCode hash = new();
                hash.AddBytes(new Span<byte>(__self, sizeof(example_struct_t)));
                return hash.ToHashCode();
            }
        }
    }

    public partial struct InlineArrays
    {
        public partial struct uint_4 : IEquatable<uint_4>
        {
            public bool Equals(uint_4 other)
            {
                fixed (uint_4* __self = &this)
                {
                    return new Span<byte>(__self, sizeof(uint_4)).SequenceEqual(new Span<byte>(&other, sizeof(uint_4)));
                }
            }

            public override bool Equals(object? obj)
            {
                return obj is uint_4 other && Equals(other);
            }

            public static bool operator ==(uint_4 left, uint_4 right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(uint_4 left, uint_4 right)
            {
                return !(left == right);
            }

            public override int GetHashCode()
            {
                fixed (uint_4* __self = &this)
                {
                    HashCode hash = new();
                    hash.AddBytes(new Span<byte>(__self, sizeof(uint_4)));
                    return hash.ToHashCode();
                }
            }
        }
    }
}
#pragma warning restore CA1069
#nullable disable
