using System.Reflection;
using System.Runtime.InteropServices;
using Bindgen.NET;
using ClangSharp;
using Type = System.Type;
using ClangSharp.Interop;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace Bindgen.NET.Tests;

// A small C header exercising the risky features of the generator: bit-fields (mixed widths,
// a run spanning storage units, an unaligned run), DEBUG-conditional fields, plain enums,
// a keyword-named function and a union.
public sealed class BindingGeneratorTests
{
    private const string Header = """
        #include <stdint.h>

        #ifndef TEST_NDEBUG
        #define TEST_DEBUG
        #endif

        typedef struct {
            uint32_t a;
            uint32_t b : 26;
            uint32_t c : 6;
        } bf_mixed_t;

        typedef struct {
            uint32_t f1 : 20;
            uint32_t f2 : 20;
        } bf_span_t;

        typedef struct {
            char ch;
            uint32_t small : 7;
        } bf_unaligned_t;

        typedef struct {
            int64_t x;
            uint64_t big : 40;
            uint64_t big2 : 24;
        } bf_long_t;

        typedef struct {
            _Bool b : 1;
            _Bool b2 : 1;
            uint32_t x;
        } bf_bool_t;

        typedef struct {
            uint32_t a;
        #ifdef TEST_DEBUG
            uint32_t dbg;
        #endif
            uint32_t y;
        } cond_t;

        typedef enum { pe_a = 1, pe_b } plain_enum_t;
        typedef enum { ne_a = -1 } neg_enum_t;

        typedef struct {
            int32_t s : 5;
            neg_enum_t e : 3;
            uint32_t u : 5;
        } bf_signed_t;

        int32_t fixed(int32_t value);

        typedef union {
            uint32_t u32;
            uint8_t bytes[4];
        } uni_t;
        """;

    private const string ConditionalSymbol = "TEST_DEBUG";
    private static readonly string[] StructNames = { "bf_mixed_t", "bf_span_t", "bf_unaligned_t", "bf_long_t", "bf_bool_t", "bf_signed_t", "cond_t", "uni_t" };

    private static string Generate(string source, Action<BindingOptions>? configure = null)
    {
        BindingOptions options = new()
        {
            Namespace = "Bindings",
            Class = "Native",
            DllImportPath = "testlib",
            TreatInputFileAsRawSourceCode = true,
            InputFile = source,
            RawSourceName = "test.h",
        };

        configure?.Invoke(options);
        return BindingGenerator.Generate(options);
    }

    private static Assembly Compile(string source, params string[] preprocessorSymbols)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp13)
            .WithPreprocessorSymbols(preprocessorSymbols.Concat(new[] { "TEST" }));

        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] references = Directory.GetFiles(runtimeDirectory, "*.dll")
            .Select(file => MetadataReference.CreateFromFile(file))
            .Cast<MetadataReference>()
            .ToArray();

        CSharpCompilation compilation = CSharpCompilation.Create(
            "BindingsTestsOutput",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        using MemoryStream stream = new();
        EmitResult result = compilation.Emit(stream);

        Assert.True(result.Success, string.Join('\n', result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        return Assembly.Load(stream.ToArray());
    }

    private static string GetAccessors() => """
            public static unsafe class __Layout
            {
                public static int __sizeof(string name) => name switch
                {
            """ + string.Join("\n", StructNames.Select(name => $"                \"{name}\" => sizeof(Bindings.Native.{name}),"))
            + """
                    _ => 0,
                };
            }
            """;

    private static Assembly GenerateAndCompile(params string[] preprocessorSymbols) =>
        Compile(Generate(Header) + GetAccessors(), preprocessorSymbols);

    // The C layout, taken from clang itself so the generated C# is checked against the real ABI.
    private static (long Size, List<(string Name, long BitOffset, bool IsBitfield)> Fields) GetCLayout(string structName, params string[] defines)
    {
        CXIndex index = CXIndex.Create();
        try
        {
            ClangSharp.Interop.CXTranslationUnit.TryParse(index, "test.h", defines.Select(define => "-D" + define).ToArray(), new CXUnsavedFile[] { CXUnsavedFile.Create("test.h", Header) }, ClangSharp.Interop.CXTranslationUnit_Flags.CXTranslationUnit_None, out CXTranslationUnit handle);
            TranslationUnit translationUnit = TranslationUnit.GetOrCreate(handle);

            RecordDecl? record = null;

            void Walk(Cursor cursor)
            {
                foreach (ClangSharp.Cursor child in cursor.CursorChildren)
                {
                    if (child is RecordDecl recordDecl && recordDecl.Name == structName)
                        record = recordDecl;

                    Walk(child);
                }
            }

            Walk(translationUnit.TranslationUnitDecl);

            RecordDecl found = Assert.IsType<RecordDecl>(record);
            List<(string, long, bool)> fields = found.Decls.OfType<FieldDecl>()
                .Where(field => !string.IsNullOrEmpty(field.Name))
                .Select(field => (field.Name, field.Handle.OffsetOfField, field.IsBitField))
                .ToList();

            return (found.TypeForDecl.Handle.SizeOf, fields);
        }
        finally
        {
            index.Dispose();
        }
    }

    private static Type GetStructType(Assembly assembly, string name) => assembly.GetType($"Bindings.Native+{name}")!;

    private static int SizeOf(Assembly assembly, string name) => (int)assembly.GetType("Bindings.__Layout")!.GetMethod("__sizeof")!.Invoke(null, new object[] { name })!;

    [Fact]
    public void BitfieldLayoutMatchesC()
    {
        Assembly assembly = GenerateAndCompile();

        foreach (string name in new[] { "bf_mixed_t", "bf_span_t", "bf_unaligned_t", "bf_long_t", "bf_bool_t", "bf_signed_t", "uni_t" })
        {
            (long cSize, _) = GetCLayout(name);
            Assert.Equal(cSize, SizeOf(assembly, name));
        }
    }

    [Fact]
    public void FieldOffsetsMatchC()
    {
        Assembly assembly = GenerateAndCompile();

        foreach (string name in new[] { "bf_mixed_t", "bf_span_t", "bf_unaligned_t", "bf_long_t", "bf_bool_t", "bf_signed_t", "uni_t" })
        {
            Type type = GetStructType(assembly, name);
            (_, List<(string Name, long BitOffset, bool IsBitfield)> fields) = GetCLayout(name);

            foreach ((string fieldName, long bitOffset, bool isBitfield) in fields)
            {
                // Bit-fields are properties over a backing buffer, so only regular fields have offsets.
                if (isBitfield)
                    continue;

                FieldInfo? field = type.GetField(fieldName);
                Assert.NotNull(field);
                Assert.Equal(bitOffset / 8, (long)Marshal.OffsetOf(type, fieldName));
            }
        }
    }

    [Fact]
    public void BitfieldPropertiesRoundTrip()
    {
        Assembly assembly = GenerateAndCompile();
        object instance = Activator.CreateInstance(GetStructType(assembly, "bf_mixed_t"))!;

        PropertyInfo b = instance.GetType().GetProperty("b")!;
        PropertyInfo c = instance.GetType().GetProperty("c")!;

        b.SetValue(instance, 0x3FFFFFFu);
        c.SetValue(instance, 0x3Fu);

        Assert.Equal(0x3FFFFFFu, b.GetValue(instance));
        Assert.Equal(0x3Fu, c.GetValue(instance));
    }

    [Fact]
    public void BitfieldsAtUnalignedOffsetRoundTrip()
    {
        Assembly assembly = GenerateAndCompile();
        object instance = Activator.CreateInstance(GetStructType(assembly, "bf_unaligned_t"))!;

        PropertyInfo small = instance.GetType().GetProperty("small")!;

        small.SetValue(instance, 0x7Fu);
        Assert.Equal(0x7Fu, small.GetValue(instance));
    }
    [Fact]
    public void BitfieldsEndingAtBit64RoundTrip()
    {
        Assembly assembly = GenerateAndCompile();
        object instance = Activator.CreateInstance(GetStructType(assembly, "bf_long_t"))!;

        PropertyInfo big = instance.GetType().GetProperty("big")!;
        PropertyInfo big2 = instance.GetType().GetProperty("big2")!;

        big.SetValue(instance, 0xFFFFFFFFFFul);
        Assert.Equal(0xFFFFFFFFFFul, big.GetValue(instance));

        // big2 ends at bit 64 of the run; setting it must not wipe the fields before it.
        big2.SetValue(instance, 0xFFFFFFul);
        Assert.Equal(0xFFFFFFul, big2.GetValue(instance));
        Assert.Equal(0xFFFFFFFFFFul, big.GetValue(instance));

        big2.SetValue(instance, 1ul);
        Assert.Equal(1ul, big2.GetValue(instance));
        Assert.Equal(0xFFFFFFFFFFul, big.GetValue(instance));
    }

    [Fact]
    public void BoolBitfieldsCompileAndRoundTripInByteMode()
    {
        // Default options: _Bool maps to byte, so the properties must be byte-typed.
        Assembly assembly = GenerateAndCompile();
        object instance = Activator.CreateInstance(GetStructType(assembly, "bf_bool_t"))!;

        PropertyInfo b = instance.GetType().GetProperty("b")!;
        PropertyInfo b2 = instance.GetType().GetProperty("b2")!;

        Assert.Equal(typeof(byte), b.PropertyType);

        b.SetValue(instance, (byte)1);
        Assert.Equal((byte)1, b.GetValue(instance));

        b2.SetValue(instance, (byte)1);
        Assert.Equal((byte)1, b2.GetValue(instance));
        Assert.Equal((byte)1, b.GetValue(instance));
    }

    [Fact]
    public void SignedBitfieldsSignExtend()
    {
        Assembly assembly = GenerateAndCompile();
        object instance = Activator.CreateInstance(GetStructType(assembly, "bf_signed_t"))!;

        PropertyInfo s = instance.GetType().GetProperty("s")!;
        PropertyInfo e = instance.GetType().GetProperty("e")!;
        PropertyInfo u = instance.GetType().GetProperty("u")!;

        // All-ones bits in a signed field must read back as -1, not as a zero-extended positive value.
        s.SetValue(instance, -1);
        Assert.Equal(-1, s.GetValue(instance));

        s.SetValue(instance, 7);
        u.SetValue(instance, 31u);
        Assert.Equal(7, s.GetValue(instance));
        Assert.Equal(31u, u.GetValue(instance));

        object negOne = Enum.ToObject(e.PropertyType, -1);
        e.SetValue(instance, negOne);
        Assert.Equal(negOne, e.GetValue(instance));
    }

    [Fact]
    public void DuplicateDeclarationsWithConditionalSymbolDoNotThrow()
    {
        string source = Header + "\nint32_t dup(int32_t value);\nint32_t dup(int32_t value);\n";

        string generated = Generate(source, options =>
        {
            options.ConditionalSymbol = ConditionalSymbol;
            options.ConditionalDefines.Add("TEST_NDEBUG");
        });

        // A legal C redeclaration must still be emitted exactly once.
        Assert.Equal(1, generated.Split("extern int dup(int value)").Length - 1);
        Compile(generated + GetAccessors(), ConditionalSymbol);
    }

    [Fact]
    public void RedefinedMacroEmitsLastValue()
    {
        string generated = Generate(Header + "\n#define RMAC 1\n#undef RMAC\n#define RMAC 2\n");

        Assert.Contains("RMAC = 2", generated);
        Assert.DoesNotContain("RMAC = 1", generated);
    }
    [Fact]
    public void ConditionalLayoutMatchesBothParses()
    {
        string generated = Generate(Header, options =>
        {
            options.ConditionalSymbol = ConditionalSymbol;
            options.ConditionalDefines.Add("TEST_NDEBUG");
        });

        Assert.Contains($"#if {ConditionalSymbol}", generated);

        Assembly debug = Compile(generated + GetAccessors(), ConditionalSymbol);
        Assembly release = Compile(generated + GetAccessors());

        (long debugSize, _) = GetCLayout("cond_t");
        (long releaseSize, _) = GetCLayout("cond_t", "TEST_NDEBUG");

        Assert.Equal(debugSize, SizeOf(debug, "cond_t"));
        Assert.Equal(releaseSize, SizeOf(release, "cond_t"));
        Assert.Equal(4L, debugSize - releaseSize);
        Assert.NotNull(GetStructType(debug, "cond_t").GetField("dbg"));
        Assert.Null(GetStructType(release, "cond_t").GetField("dbg"));
    }

    [Fact]
    public void EnumUnderlyingTypeMatchesC()
    {
        Assembly assembly = GenerateAndCompile();

        // clang gives plain C enums an unsigned underlying type when all values are non-negative,
        // and a signed one once a negative value appears.
        Assert.Equal(typeof(uint), GetStructType(assembly, "plain_enum_t").GetEnumUnderlyingType());
        Assert.Equal(typeof(int), GetStructType(assembly, "neg_enum_t").GetEnumUnderlyingType());
    }

    [Fact]
    public void KeywordNamedFunctionUsesRawEntryPoint()
    {
        Assembly assembly = GenerateAndCompile();
        MethodInfo method = GetStructType(assembly, "uni_t").DeclaringType!.GetMethod("fixed")!;

        Assert.NotNull(method);

        CustomAttributeData attribute = method.GetCustomAttributesData()
            .Single(attributeData => attributeData.AttributeType == typeof(DllImportAttribute));

        string entryPoint = attribute.ConstructorArguments.Count > 1
            ? (string)attribute.ConstructorArguments[1].Value!
            : (string)attribute.NamedArguments.Single(argument => argument.MemberName == "EntryPoint").TypedValue.Value!;

        Assert.Equal("fixed", entryPoint);
    }

    [Fact]
    public void UnionLayoutMatchesC()
    {
        Assembly assembly = GenerateAndCompile();
        Type type = GetStructType(assembly, "uni_t");

        Assert.Equal(4, SizeOf(assembly, "uni_t"));
        Assert.Equal(0L, (long)Marshal.OffsetOf(type, "u32"));
    }
}

