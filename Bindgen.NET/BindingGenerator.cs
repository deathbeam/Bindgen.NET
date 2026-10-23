using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ClangSharp;
using ClangSharp.Interop;
using Type = ClangSharp.Type;

namespace Bindgen.NET;

/// <summary>
/// Static class for generating bindings from configuration classes.
/// </summary>
public static class BindingGenerator
{
    private const string MacroPrefix = "BindgenMacro";
    private const string AnonymousPrefix = "Anonymous";

    private static BindingOptions Options = new();

    private static Dictionary<Type, string> TypeNames = new();
    private static Dictionary<Type, string> TypeFullNames = new();

    /// <summary>
    /// A single generated declaration. Declarations are collected separately so that the output of a primary
    /// parse and an alternate parse (see <see cref="BindingOptions.ConditionalSymbol"/>) can be merged.
    /// </summary>
    private sealed class Declaration
    {
        public required string Kind { get; init; }

        public required string Name { get; init; }

        public required string Text { get; init; }

        // Text that belongs in the native helper file instead of the managed output.
        public string? NativeText { get; init; }

        // Only set for records: per-member emissions, so records can be merged at member granularity
        // (for fields that only exist in one of the two conditional parses).
        public string? Header { get; init; }

        public string? Footer { get; init; }

        public IReadOnlyList<KeyValuePair<string, string>>? Members { get; init; }
    }

    /// <summary>
    /// Generates bindings based on the values specified in the <c>options</c> parameter.
    /// </summary>
    /// <param name="options">The configuration options to use when generating bindings.</param>
    /// <returns>A string of the generated source code.</returns>
    public static string Generate(BindingOptions options)
    {
        Options = options;

        TranslationUnit? primaryTranslationUnit = null;
        CXIndex? primaryIndex = null;
        TranslationUnit? alternateTranslationUnit = null;
        CXIndex? alternateIndex = null;

        try
        {
            (primaryTranslationUnit, primaryIndex) = ProcessTranslationUnit(withConditionalDefines: false);
            List<Declaration> declarations = GenerateDeclarations(primaryTranslationUnit);

            primaryTranslationUnit.Dispose();
            primaryIndex?.Dispose();
            primaryTranslationUnit = null;
            primaryIndex = null;

            if (Options.ConditionalSymbol != null)
            {
                (alternateTranslationUnit, alternateIndex) = ProcessTranslationUnit(withConditionalDefines: true);
                List<Declaration> alternateDeclarations = GenerateDeclarations(alternateTranslationUnit);

                alternateTranslationUnit.Dispose();
                alternateIndex?.Dispose();
                alternateTranslationUnit = null;
                alternateIndex = null;

                declarations = MergeDeclarations(declarations, alternateDeclarations);
            }

            StringBuilder outputBuilder = new();
            StringBuilder nativeOutputBuilder = new();

            foreach (Declaration declaration in declarations)
            {
                if (declaration.Text.Length != 0)
                    outputBuilder.AppendLine(declaration.Text);

                if (declaration.NativeText is { Length: > 0 } nativeText)
                    nativeOutputBuilder.AppendLine(nativeText);
            }

            string output = CodeFormatter.Format($$"""
                #nullable enable
                {{(Options.SuppressedWarnings.Count > 0 ? $"#pragma warning disable {string.Join(' ', Options.SuppressedWarnings)}" : string.Empty)}}
                using System;
                using System.Runtime.InteropServices;
                using System.Runtime.CompilerServices;
                
                {{(Options.GenerateDisableRuntimeMarshallingAttribute ? "[assembly: DisableRuntimeMarshalling]" : "")}}
                
                namespace {{Options.Namespace}};
                
                public static unsafe partial class {{Options.Class}}
                {
                    {{GenerateBindgenInternal()}}
                    {{outputBuilder}}
                }
                {{(Options.SuppressedWarnings.Count > 0 ? $"#pragma warning restore {string.Join(' ', Options.SuppressedWarnings)}" : string.Empty)}}
                #nullable disable
            """);

            string nativeOutput = $$"""
            #ifdef _WIN32
                #define BINDGEN_API __declspec(dllexport)
            #else
                #define BINDGEN_API __attribute__((visibility("default")))
            #endif
            {{nativeOutputBuilder}}
            """;


            if (options.OutputFile != null)
            {
                File.WriteAllText(options.OutputFile, output);
                Diagnostic.Log(DiagnosticLevel.Info, $"Generated {Path.GetFullPath(options.OutputFile)} from {GetInputFileName()}");
            }

            if (options.NativeOutputFile != null)
            {
                File.WriteAllText(options.NativeOutputFile, nativeOutput);
                Diagnostic.Log(DiagnosticLevel.Info, $"Generated {Path.GetFullPath(options.NativeOutputFile)} from {GetInputFileName()}");
            }

            return output;
        }
        finally
        {
            primaryTranslationUnit?.Dispose();
            primaryIndex?.Dispose();
            alternateTranslationUnit?.Dispose();
            alternateIndex?.Dispose();

            TypeNames = new Dictionary<Type, string>();
            TypeFullNames = new Dictionary<Type, string>();
        }
    }

    private static List<Declaration> GenerateDeclarations(TranslationUnit translationUnit)
    {
        Cursor[] cursors = translationUnit.TranslationUnitDecl.CursorChildren
            .Where(cursor => cursor is FunctionDecl or RecordDecl or EnumDecl or VarDecl)
            .Where(cursor => !cursor.Location.IsInSystemHeader)
            .Where(IsUserInclude)
            .Where(cursor => !Options.Ignored.Contains(cursor.Handle.Spelling.CString))
            .ToArray();

        IEnumerable<ConstantArrayType> constantArrayTypes = GetConstantArrayTypes(cursors);

        IEnumerable<RecordDecl> recordDecls = GetRecordDecls(cursors);

        IEnumerable<FunctionDecl> functionDecls = cursors
            .OfType<FunctionDecl>()
            .GroupBy(x => x.Name) // C allows redeclaring functions and extern variables; keep the first declaration.
            .Select(group => group.First())
            .OrderBy(x => x.Name)
            .ToArray();

        IEnumerable<EnumDecl> enumDecls = cursors
            .OfType<EnumDecl>()
            .OrderBy(x => x.Name);

        IEnumerable<VarDecl> varDecls = cursors
            .OfType<VarDecl>()
            .GroupBy(x => x.Name)
            .Select(group => group.First())
            .OrderBy(x => x.Name);

        IEnumerable<VarDecl> macroVarDecls = varDecls
            .Where(x => x.Name.StartsWith(MacroPrefix, StringComparison.Ordinal));

        IEnumerable<VarDecl> externVarDecls = varDecls
            .Where(x => x.HasExternalStorage);

        List<Declaration> declarations = new();

        foreach (FunctionDecl functionDecl in functionDecls)
            declarations.Add(new Declaration { Kind = "Function", Name = functionDecl.Name, Text = GenerateFunctionDecl(functionDecl) });

        foreach (EnumDecl enumDecl in enumDecls)
            declarations.Add(new Declaration { Kind = "Enum", Name = enumDecl.Name, Text = GenerateEnumDecl(enumDecl) });

        foreach (EnumDecl enumDecl in enumDecls)
            declarations.Add(new Declaration { Kind = "EnumConstants", Name = enumDecl.Name, Text = GenerateEnumDeclConstants(enumDecl) });

        foreach (RecordDecl recordDecl in recordDecls)
            declarations.Add(GenerateRecordDeclaration(recordDecl));

        foreach (ConstantArrayType type in constantArrayTypes)
            declarations.Add(new Declaration { Kind = "ConstantArray", Name = GetTypeName(type), Text = GenerateConstantArrayType(type) });

        foreach (VarDecl varDecl in macroVarDecls)
            declarations.Add(new Declaration { Kind = "MacroVariable", Name = varDecl.Name, Text = GenerateMacroVarDecl(varDecl) });

        if (Options.GenerateExternVariables)
        {
            foreach (VarDecl varDecl in externVarDecls)
                declarations.Add(new Declaration { Kind = "ExternVariableGetter", Name = varDecl.Name, Text = GenerateExternVarDeclManagedGetter(varDecl) });

            foreach (VarDecl varDecl in externVarDecls)
                declarations.Add(new Declaration { Kind = "ExternVariableField", Name = varDecl.Name, Text = GenerateExternVarDeclField(varDecl) });

            foreach (VarDecl varDecl in externVarDecls)
                declarations.Add(new Declaration { Kind = "ExternVariableProperty", Name = varDecl.Name, Text = GenerateExternVarDeclProperty(varDecl) });

            foreach (VarDecl varDecl in externVarDecls)
                declarations.Add(new Declaration { Kind = "ExternVariableNative", Name = varDecl.Name, Text = string.Empty, NativeText = GenerateExternVarDeclNativeVariable(varDecl) });

            foreach (VarDecl varDecl in externVarDecls)
                declarations.Add(new Declaration { Kind = "ExternVariableNativeGetter", Name = varDecl.Name, Text = string.Empty, NativeText = GenerateExternVarDeclNativeGetter(varDecl) });
        }

        foreach (RecordDecl recordDecl in recordDecls)
            declarations.Add(new Declaration { Kind = "RecordEquality", Name = GetTypeName(recordDecl.TypeForDecl), Text = GenerateRecordEqualityMethods(recordDecl.TypeForDecl) });

        foreach (ConstantArrayType type in constantArrayTypes)
            declarations.Add(new Declaration { Kind = "ConstantArrayEquality", Name = GetTypeName(type), Text = GenerateConstantArrayTypeEqualityMethods(type) });

        return declarations;
    }

    // Merges the declarations of the primary and the alternate parse into a single declaration list:
    // declarations that only exist in the primary parse are wrapped in "#if SYMBOL", declarations that only
    // exist in the alternate parse are wrapped in "#if !SYMBOL", and differing records are merged per member.
    private static List<Declaration> MergeDeclarations(List<Declaration> primary, List<Declaration> alternate)
    {
        string symbol = Options.ConditionalSymbol!;

        Dictionary<(string Kind, string Name), Declaration> alternateByKey = alternate
            .ToDictionary(declaration => (declaration.Kind, declaration.Name));

        HashSet<(string Kind, string Name)> primaryKeys = primary
            .Select(declaration => (declaration.Kind, declaration.Name))
            .ToHashSet();

        List<Declaration> merged = new();

        foreach (Declaration declaration in primary)
        {
            if (!alternateByKey.TryGetValue((declaration.Kind, declaration.Name), out Declaration? alternateDeclaration))
            {
                merged.Add(WrapDeclaration(declaration, $"#if {symbol}\n{{0}}\n#endif"));
            }
            else if (declaration.Text == alternateDeclaration.Text && declaration.NativeText == alternateDeclaration.NativeText)
            {
                merged.Add(declaration);
            }
            else if (declaration.Members is not null && alternateDeclaration.Members is not null)
            {
                merged.Add(MergeRecordDeclaration(declaration, alternateDeclaration, symbol));
            }
            else
            {
                if (declaration.NativeText != alternateDeclaration.NativeText)
                    Diagnostic.Log(DiagnosticLevel.Warning, $"Native output for '{declaration.Name}' differs between the primary and the alternate parse; the primary variant is used.");

                merged.Add(WrapDeclaration(declaration, $"#if {symbol}\n{{0}}\n#else\n{alternateDeclaration.Text}\n#endif"));
            }
        }

        foreach (Declaration declaration in alternate)
        {
            if (!primaryKeys.Contains((declaration.Kind, declaration.Name)))
                merged.Add(WrapDeclaration(declaration, $"#if !{symbol}\n{{0}}\n#endif"));
        }

        return merged;
    }

    private static Declaration WrapDeclaration(Declaration declaration, string format)
    {
        string text = declaration.Text.Length == 0 ? declaration.Text : string.Format(CultureInfo.InvariantCulture, format, declaration.Text);
        return new Declaration { Kind = declaration.Kind, Name = declaration.Name, Text = text, NativeText = declaration.NativeText };
    }

    private static Declaration MergeRecordDeclaration(Declaration primary, Declaration alternate, string symbol)
    {
        HashSet<string> primaryKeys = primary.Members!.Select(member => member.Key).ToHashSet();
        HashSet<string> alternateKeys = alternate.Members!.Select(member => member.Key).ToHashSet();
        Dictionary<string, string> alternateByKey = alternate.Members!.ToDictionary(member => member.Key, member => member.Value);
        IReadOnlyList<KeyValuePair<string, string>> primaryMembers = primary.Members!;
        IReadOnlyList<KeyValuePair<string, string>> alternateMembers = alternate.Members!;

        List<KeyValuePair<string, string>> mergedMembers = new();

        int i = 0;
        int j = 0;

        while (i < primaryMembers.Count || j < alternateMembers.Count)
        {
            KeyValuePair<string, string> primaryMember = i < primaryMembers.Count ? primaryMembers[i] : default;
            KeyValuePair<string, string> alternateMember = j < alternateMembers.Count ? alternateMembers[j] : default;

            if (i < primaryMembers.Count && j < alternateMembers.Count && primaryMember.Key == alternateMember.Key)
            {
                mergedMembers.Add(primaryMember.Value == alternateMember.Value
                    ? primaryMember
                    : new KeyValuePair<string, string>(primaryMember.Key, $"#if {symbol}\n{primaryMember.Value}\n#else\n{alternateMember.Value}\n#endif"));

                i++;
                j++;
            }
            else if (i < primaryMembers.Count && !alternateKeys.Contains(primaryMember.Key))
            {
                mergedMembers.Add(new KeyValuePair<string, string>(primaryMember.Key, $"#if {symbol}\n{primaryMember.Value}\n#endif"));
                i++;
            }
            else if (j < alternateMembers.Count && !primaryKeys.Contains(alternateMember.Key))
            {
                mergedMembers.Add(new KeyValuePair<string, string>(alternateMember.Key, $"#if !{symbol}\n{alternateMember.Value}\n#endif"));
                j++;
            }
            else
            {
                // Both parses contain this member but not at the same position; keep the primary variant.
                mergedMembers.Add(new KeyValuePair<string, string>(primaryMember.Key, $"#if {symbol}\n{primaryMember.Value}\n#else\n{alternateByKey[primaryMember.Key]}\n#endif"));
                i++;
                j++;
            }
        }

        string header = primary.Header == alternate.Header
            ? primary.Header!
            : $"#if {symbol}\n{primary.Header}\n#else\n{alternate.Header}\n#endif";

        string text = $"{header}\n{string.Join("\n", mergedMembers.Select(member => member.Value))}\n{primary.Footer}";

        return new Declaration
        {
            Kind = primary.Kind,
            Name = primary.Name,
            Text = text,
            Header = primary.Header,
            Footer = primary.Footer,
            Members = mergedMembers,
        };
    }

    private static (TranslationUnit, CXIndex) ProcessTranslationUnit(bool withConditionalDefines)
    {
        Diagnostic.CurrentDiagnosticLevel = Options.DiagnosticLevel;

        string inputFileName = GetInputFileName();

        List<string> arguments = Options.IncludeDirectories
            .Union(Options.SystemIncludeDirectories)
            .Select(includeDirectory => "-I" + Path.GetFullPath(includeDirectory))
            .ToList();

        bool addConditionalDefines = Options.ConditionalSymbol != null
            ? withConditionalDefines
            : Options.ConditionalDefines.Count > 0;

        if (addConditionalDefines)
            arguments.AddRange(Options.ConditionalDefines.Select(define => "-D" + define));

        List<CXUnsavedFile> unsavedFiles = new();
        CXTranslationUnit_Flags flags = CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord;

        if (Options.TreatInputFileAsRawSourceCode)
            unsavedFiles.Add(CXUnsavedFile.Create(inputFileName, Options.InputFile));
        else if (!Path.Exists(inputFileName))
            throw new ArgumentException($"Input file at path \"{inputFileName}\" does not exist.");

        CXIndex index = CXIndex.Create();
        CXErrorCode errorCode = CXTranslationUnit.TryParse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags, out CXTranslationUnit handle);

        foreach (CXUnsavedFile unsavedFile in unsavedFiles)
            unsavedFile.Dispose();

        ProcessDiagnostics(errorCode, handle);

        TranslationUnit translationUnit = TranslationUnit.GetOrCreate(handle);

        translationUnit = ProcessMacros(index, translationUnit, arguments.ToArray(), flags);

        return (translationUnit, index);
    }

    private static string GetInputFileName()
    {
        return Options.TreatInputFileAsRawSourceCode ? Options.RawSourceName : Path.GetFullPath(Options.InputFile);
    }

    private static void ProcessDiagnostics(CXErrorCode errorCode, CXTranslationUnit handle)
    {
        if (errorCode != CXErrorCode.CXError_Success)
            throw new InvalidOperationException($"Failed to parse translation unit (clang error code: {errorCode}).");

        if (handle.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to parse translation unit.");

        for (uint i = 0; i < handle.NumDiagnostics; i++)
        {
            using CXDiagnostic diagnostic = handle.GetDiagnostic(i);
            string message = diagnostic.Format(CXDiagnostic.DefaultDisplayOptions).ToString();

            DiagnosticLevel level = diagnostic.Severity switch
            {
                CXDiagnosticSeverity.CXDiagnostic_Error or CXDiagnosticSeverity.CXDiagnostic_Fatal => DiagnosticLevel.Error,
                CXDiagnosticSeverity.CXDiagnostic_Warning => DiagnosticLevel.Warning,
                _ => DiagnosticLevel.Info,
            };

            Diagnostic.Log(level, message);

            if (level == DiagnosticLevel.Error)
                throw new InvalidOperationException($"Aborting generation due to parse error: {message}");
        }
    }

    // We collect all macro definition records and append them to the end of the file as type-inferred auto variables.
    // Some macros alias a type or an attribute and produce invalid dummy declarations; those never yield a VarDecl
    // anyway, so the declarations that fail to parse are dropped and the file is re-parsed. Errors anywhere else
    // (i.e. in the user's code) abort the generation.
    private static TranslationUnit ProcessMacros(CXIndex index, TranslationUnit translationUnit, ReadOnlySpan<string> arguments, CXTranslationUnit_Flags flags)
    {
        const int maxAttempts = 4;

        string inputFileName = GetInputFileName();
        CXTranslationUnit translationUnitHandle = translationUnit.Handle;

        CXFile file = translationUnitHandle.GetFile(inputFileName);
        ReadOnlySpan<byte> inputFileContents = translationUnitHandle.GetFileContents(file, out UIntPtr _);

        string inputFileText = Encoding.UTF8.GetString(inputFileContents);
        string headerText = inputFileText.EndsWith('\n') ? inputFileText : inputFileText + "\n";
        int originalLineCount = headerText.Count(c => c == '\n');

        MacroDefinitionRecord[] macroDefinitionRecords = translationUnit.TranslationUnitDecl.CursorChildren
            .OfType<MacroDefinitionRecord>()
            .Where(macro => !macro.Location.IsInSystemHeader)
            .Where(macro => !IsFromNamelessFile(macro))
            .Where(IsUserInclude)
            // A macro redefined through #undef/#define contributes only its last definition; keeping an earlier
            // one too would make the later dummy a redefinition error that the retry logic silently drops.
            .GroupBy(macro => macro.Name)
            .Select(group => group.Last())
            .ToArray();

        List<(int StartLine, int LineCount, string Text)> dummies = new();
        int line = originalLineCount + 1;

        foreach (MacroDefinitionRecord macroDefinitionRecord in macroDefinitionRecords)
        {
            string dummy = GenerateMacroDummy(macroDefinitionRecord);

            if (dummy.Length == 0)
                continue;

            int lineCount = dummy.Count(c => c == '\n') + 1;
            dummies.Add((line, lineCount, dummy));
            line += lineCount;
        }

        translationUnit.Dispose();

        List<CXUnsavedFile> unsavedFiles = new();
        CXTranslationUnit handle = default;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            unsavedFiles.Add(CXUnsavedFile.Create(inputFileName, headerText + string.Join("\n", dummies.Select(dummy => dummy.Text)) + "\n"));

            handle = CXTranslationUnit.Parse(index, inputFileName, arguments.ToArray(), unsavedFiles.ToArray(), flags & ~CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord);

            foreach (CXUnsavedFile unsavedFile in unsavedFiles)
                unsavedFile.Dispose();
            unsavedFiles.Clear();

            HashSet<int> errorLines = GetParseErrorLines(handle, inputFileName);

            if (errorLines.Count == 0)
                break;

            // Drop the dummy declarations that the errors point at; errors that don't belong to any dummy
            // (or errors that survive the drops) fail generation through ProcessDiagnostics below.
            List<(int, int, string)> remaining = dummies
                .Where(dummy => !Enumerable.Range(dummy.StartLine, dummy.LineCount).Any(errorLines.Contains))
                .ToList();

            if (remaining.Count == dummies.Count)
                break;

            dummies = remaining;
        }

        ProcessDiagnostics(CXErrorCode.CXError_Success, handle);

        return TranslationUnit.GetOrCreate(handle);
    }

    private static HashSet<int> GetParseErrorLines(CXTranslationUnit handle, string inputFileName)
    {
        HashSet<int> errorLines = new();

        for (uint i = 0; i < handle.NumDiagnostics; i++)
        {
            using CXDiagnostic diagnostic = handle.GetDiagnostic(i);

            if (diagnostic.Severity is not (CXDiagnosticSeverity.CXDiagnostic_Error or CXDiagnosticSeverity.CXDiagnostic_Fatal))
                continue;

            diagnostic.Location.GetFileLocation(out CXFile file, out _, out _, out _);

            if (file.Name.ToString() == inputFileName)
            {
                diagnostic.Location.GetFileLocation(out _, out uint lineNumber, out _, out _);
                errorLines.Add((int)lineNumber);
            }
        }

        return errorLines;
    }

    private static string GetSourceRangeContents(CXTranslationUnit translationUnit, CXSourceRange sourceRange)
    {
        sourceRange.Start.GetFileLocation(out CXFile startFile, out uint _, out uint _, out uint startOffset);
        sourceRange.End.GetFileLocation(out CXFile endFile, out uint _, out uint _, out uint endOffset);

        if (startFile != endFile)
            return string.Empty;

        ReadOnlySpan<byte> fileContents = translationUnit.GetFileContents(startFile, out UIntPtr _);
        fileContents = fileContents.Slice(unchecked((int)startOffset), unchecked((int)(endOffset - startOffset)));

        return Encoding.UTF8.GetString(fileContents);
    }

    // We don't want to generate bindings for stuff inside of system includes. We use this to filter for non-system headers.
    private static bool IsUserInclude(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out _, out _, out _);
        string fileName = file.Name.ToString();

        return Options.SystemIncludeDirectories
            .Select(Path.GetFullPath)
            .All(fullIncludeDirectory => !IsInDirectory(fileName, fullIncludeDirectory));
    }

    private static bool IsInDirectory(string fileName, string directory)
    {
        return fileName.Length > directory.Length
            && fileName.StartsWith(directory, StringComparison.Ordinal)
            && (fileName[directory.Length] == Path.DirectorySeparatorChar || fileName[directory.Length] == Path.AltDirectorySeparatorChar);
    }

    // Don't generate macros from files with empty names because it includes some junk.
    private static bool IsFromNamelessFile(Cursor cursor)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint _, out uint _, out uint _);
        return string.IsNullOrEmpty(file.Name.ToString());
    }

    private static bool RecordHasDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
                return false;

            recordDecl = recordDecl.Definition;
        }

        return true;
    }

    private static RecordDecl GetRecordDefinition(RecordDecl recordDecl)
    {
        while (!recordDecl.IsThisDeclarationADefinition)
        {
            if (recordDecl.Definition == null)
                break;

            recordDecl = recordDecl.Definition;
        }

        return recordDecl;
    }

    private static bool IsType<T>(Type type, [MaybeNullWhen(false)] out T value) where T : Type?
    {
        if (type is T t)
        {
            value = t;
            return true;
        }

        if (type is ElaboratedType elaboratedType)
            return IsType(elaboratedType.CanonicalType, out value);

        if (type is PointerType pointerType)
            return IsType(pointerType.PointeeType, out value);

        if (type is ConstantArrayType constantArrayType)
            return IsType(constantArrayType.ElementType, out value);

        value = default;
        return false;
    }

    private static IEnumerable<RecordDecl> GetRecordDecls(IEnumerable<Cursor> cursors)
    {
        return cursors
            .SelectMany(Recurse)
            .GroupBy(cursor => cursor.Handle.Spelling.CString)
            .Select(group => group.First())
            .OrderBy(recordDecl => GetTypeName(recordDecl.TypeForDecl));

        static IEnumerable<RecordDecl> Recurse(Cursor cursor)
        {
            return new[] { cursor }
                .OfType<RecordDecl>()
                .Concat(cursor.CursorChildren.SelectMany(Recurse));
        }
    }

    private static IEnumerable<ConstantArrayType> GetConstantArrayTypes(IEnumerable<Cursor> cursors)
    {
        return cursors
            .SelectMany(Recurse)
            .Distinct();

        static IEnumerable<ConstantArrayType> Recurse(Cursor cursor)
        {
            return new[] { cursor }
                .OfType<FieldDecl>()
                .Where(field => field.Type is ConstantArrayType)
                .Select(field => (ConstantArrayType)field.Type)
                .Concat(cursor.CursorChildren.SelectMany(Recurse));
        }
    }

    private static string GetCSharpFunctionPointer(FunctionProtoType functionProtoType)
    {
        List<string> parameters = functionProtoType.ParamTypes.Select(GetTypeFullName).ToList();
        parameters.Add(GetTypeFullName(functionProtoType.ReturnType));
        return $"delegate* unmanaged<{string.Join(", ", parameters)}>";
    }

    private static string GetAnonymousName(Cursor cursor, string kind)
    {
        cursor.Location.GetFileLocation(out CXFile file, out uint line, out uint column, out _);
        string fileName = Path.GetFileNameWithoutExtension(file.Name.ToString());
        return $"{AnonymousPrefix}{kind}_{fileName}_L{line}_C{column}";
    }

    private static string GetCursorName(NamedDecl namedDecl)
    {
        string name = GetValidIdentifier(namedDecl.Name);

        if (namedDecl is TypeDecl typeDecl)
        {
            bool isAnonymous =
                string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("struct (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("union (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("enum (unnamed", StringComparison.Ordinal) ||
                name.StartsWith("struct (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("union (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("enum (anonymous", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed struct", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed union", StringComparison.Ordinal) ||
                name.StartsWith("(unnamed enum", StringComparison.Ordinal);

            return isAnonymous ? GetAnonymousName(typeDecl, typeDecl.TypeForDecl.KindSpelling) : name;
        }

        return name;
    }

    private static string GetSignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "sbyte",
            2 => "short",
            4 => "int",
            8 => "long",
            _ => error ?? "INVALID_SIGNED_INTEGER"
        };
    }

    private static string GetUnsignedIntegerName(long size, string? error = null)
    {
        return size switch
        {
            1 => "byte",
            2 => "ushort",
            4 => "uint",
            8 => "ulong",
            _ => error ?? "INVALID_UNSIGNED_INTEGER"
        };
    }

    private static string GetIntegerName(long size, bool signed, string? error = null)
    {
        return signed ? GetSignedIntegerName(size, error) : GetUnsignedIntegerName(size, error);
    }

    private static string GetTypeName(Type? type)
    {
        if (type == null)
            return "UNHANDLED_TYPE";

        if (TypeNames.TryGetValue(type, out string? typeName))
            return typeName;

        if (type is RecordType recordType)
            typeName = GetCursorName(recordType.Decl);

        if (type is EnumType enumType)
            typeName = GetCursorName(enumType.Decl);

        if (type is AutoType autoType)
            typeName = GetTypeName(autoType.GetDeducedType);

        if (type is ConstantArrayType constantArrayType)
            typeName = $"{GetTypeIdentifier(constantArrayType.ElementType)}_{constantArrayType.Size}";

        if (type is FunctionProtoType functionProtoType)
            typeName = GetCSharpFunctionPointer(functionProtoType);

        if (type is IncompleteArrayType incompleteArrayType)
            typeName = GetTypeName(incompleteArrayType.ElementType) + "*";

        if (type is PointerType pointerType)
        {
            if (pointerType.CanonicalType.PointeeType is FunctionProtoType)
                typeName = GetTypeName(pointerType.PointeeType);
            else if (pointerType.PointeeType.AsString is "FILE" or "_IO_FILE" or "_iobuf" or "__sFILE"
                || (pointerType.PointeeType.CanonicalType is RecordType fileRecordType
                    && fileRecordType.Decl.Name is "_IO_FILE" or "_iobuf" or "__sFILE"))
                typeName = "void*";
            else
                typeName = GetTypeName(pointerType.CanonicalType.PointeeType) + "*";
        }

        if (type is ElaboratedType elaboratedType)
        {
            typeName = elaboratedType.AsString switch
            {
                "size_t" => "nint",
                "va_list" => "void*",
                _ => GetTypeName(elaboratedType.CanonicalType)
            };
        }

        if (type is BuiltinType builtinType)
        {
            typeName = builtinType.Kind switch
            {
                CXTypeKind.CXType_Bool => Options.GenerateDisableRuntimeMarshallingAttribute ? "bool" : "byte",
                CXTypeKind.CXType_Float => "float",
                CXTypeKind.CXType_Double => "double",
                CXTypeKind.CXType_LongDouble => "decimal",
                CXTypeKind.CXType_Void => "void",
                CXTypeKind.CXType_Char16 or
                CXTypeKind.CXType_Char32 or
                CXTypeKind.CXType_Char_S or
                CXTypeKind.CXType_Char_U or
                CXTypeKind.CXType_SChar or
                CXTypeKind.CXType_UChar or
                CXTypeKind.CXType_WChar => GetIntegerName(builtinType.Handle.SizeOf, builtinType.Handle.IsSigned, $"INVALID_CHAR_{builtinType.Kind}"),
                CXTypeKind.CXType_Short or
                CXTypeKind.CXType_Int or
                CXTypeKind.CXType_Long or
                CXTypeKind.CXType_LongLong => GetSignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_SIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}"),
                CXTypeKind.CXType_UShort or
                CXTypeKind.CXType_UInt or
                CXTypeKind.CXType_ULong or
                CXTypeKind.CXType_ULongLong => GetUnsignedIntegerName(builtinType.Handle.SizeOf, $"INVALID_UNSIGNED_INTEGER_{builtinType.Kind}_SIZEOF_{builtinType.Handle.SizeOf}"),
                _ => $"INVALID_BUILTIN_{builtinType.Kind}"
            };
        }

        TypeNames.Add(type, typeName ??= "UNHANDLED_TYPE");
        return typeName;
    }

    private static string GetTypeFullName(Type? type)
    {
        if (type == null)
            return "UNHANDLED_TYPE";

        if (TypeFullNames.TryGetValue(type, out string? typeName))
            return typeName;

        typeName = GetTypeName(type);

        if (IsType<RecordType>(type, out RecordType? recordType) && recordType.Decl.Parent is RecordDecl parent)
            typeName = $"{GetTypeFullName(parent.TypeForDecl)}.{typeName}";

        if (type is ConstantArrayType)
            typeName = $"InlineArrays.{typeName}";

        TypeFullNames.Add(type, typeName);
        return typeName;
    }

    private static string GetValidIdentifier(string identifier)
    {
        foreach ((string prefix, string replacement) in Options.RemappedPrefixes)
        {
            if (!identifier.StartsWith(prefix, StringComparison.InvariantCulture))
                continue;

            identifier = replacement + identifier[prefix.Length..];
            break;
        }

        switch (identifier)
        {
            case "abstract":
            case "as":
            case "base":
            case "bool":
            case "break":
            case "byte":
            case "case":
            case "catch":
            case "char":
            case "checked":
            case "class":
            case "const":
            case "continue":
            case "decimal":
            case "default":
            case "delegate":
            case "do":
            case "double":
            case "else":
            case "enum":
            case "event":
            case "explicit":
            case "extern":
            case "false":
            case "finally":
            case "fixed":
            case "float":
            case "for":
            case "foreach":
            case "goto":
            case "if":
            case "implicit":
            case "in":
            case "int":
            case "interface":
            case "internal":
            case "is":
            case "lock":
            case "long":
            case "namespace":
            case "new":
            case "null":
            case "object":
            case "operator":
            case "out":
            case "override":
            case "params":
            case "private":
            case "protected":
            case "public":
            case "readonly":
            case "ref":
            case "return":
            case "sbyte":
            case "sealed":
            case "short":
            case "sizeof":
            case "stackalloc":
            case "static":
            case "string":
            case "struct":
            case "switch":
            case "this":
            case "throw":
            case "true":
            case "try":
            case "typeof":
            case "uint":
            case "ulong":
            case "unchecked":
            case "unsafe":
            case "ushort":
            case "using":
            case "virtual":
            case "void":
            case "volatile":
            case "while":
                return "@" + identifier;
            default:
                return identifier;
        }
    }

    private static string GenerateExternFieldName(string name)
    {
        return name + "_Ptr";
    }

    private static string GenerateExternGetterName(string name)
    {
        return name + "_BindgenGetExtern";
    }

    private static string GenerateBindgenInternal()
    {
        return $$"""
            public partial class BindgenInternal
            {
                public const string DllImportPath = @"{{Options.DllImportPath}}";
            }
        """;
    }

    // Generates outer declarations for nested types.
    private static string GenerateOuterDeclarations(Type type, string body)
    {
        string name = GetTypeFullName(type);
        string[] outerTypes = name.Split('.')[..^1];
        return outerTypes.Length == 0 ? body : $$"""
            {{string.Concat(outerTypes.Select(outerType => $"public partial struct {outerType}{{"))}}
            {{body}}
            {{new string('}', outerTypes.Length)}}
            """;
    }

    private static string GenerateFunctionDecl(FunctionDecl functionDecl)
    {
        IEnumerable<string> parameters = functionDecl.Parameters
            .Select(parameter => $"{GetTypeFullName(parameter.Type)} {GetValidIdentifier(parameter.Name)}");

        // The entry point must use the raw C spelling; only the C# method name is keyword-escaped.
        return $@"
            {(Options.GenerateSuppressGcTransition ? "[SuppressGCTransition]" : string.Empty)}
            [DllImport(BindgenInternal.DllImportPath, EntryPoint = ""{functionDecl.Name}"")]
            public static extern {GetTypeFullName(functionDecl.ReturnType)} {GetValidIdentifier(functionDecl.Name)}({string.Join(", ", parameters)});
        ";
    }

    private static string GenerateConstantArrayType(ConstantArrayType type)
    {
        string name = GetTypeName(type.ElementType);
        string structName = GetTypeName(type);
        return GenerateOuterDeclarations(type, $$"""
            [InlineArray({{type.Size}})]
            public partial struct {{structName}}
            {
                public {{name}} Item0;
            }
            """);
    }

    private static string GenerateConstantArrayTypeEqualityMethods(ConstantArrayType type)
    {
        string name = GetTypeName(type);
        return GenerateOuterDeclarations(type, $$"""
            public partial struct {{name}} : IEquatable<{{name}}>
            {
                {{GenerateRecordEqualityFunctions(name)}}
            }
            """);
    }

    private static Declaration GenerateRecordDeclaration(RecordDecl recordDecl)
    {
        if (RecordHasDefinition(recordDecl) && !recordDecl.IsThisDeclarationADefinition)
            return GenerateRecordDeclaration(GetRecordDefinition(recordDecl));

        string recordName = GetTypeName(recordDecl.TypeForDecl);

        List<FieldDecl> fieldDecls = recordDecl.Decls.OfType<FieldDecl>().ToList();
        List<IndirectFieldDecl> indirectFieldDecls = recordDecl.Decls.OfType<IndirectFieldDecl>().ToList();

        bool hasBitfields = fieldDecls.Any(fieldDecl => fieldDecl.IsBitField);
        long recordSize = recordDecl.TypeForDecl.Handle.SizeOf;

        List<KeyValuePair<string, string>> members = new();

        int bitfieldRunCount = 0;

        for (int i = 0; i < fieldDecls.Count;)
        {
            FieldDecl fieldDecl = fieldDecls[i];

            if (!fieldDecl.IsBitField)
            {
                members.Add(new KeyValuePair<string, string>(GenerateFieldMemberName(fieldDecl), GenerateFieldDecl(recordDecl, fieldDecl, hasBitfields)));
                i++;
                continue;
            }

            if (fieldDecl.IsUnnamedBitfield && fieldDecl.BitWidthValue <= 0)
            {
                // Zero-width bit-fields are pure padding that only affects the layout of the next fields.
                i++;
                continue;
            }

            long unitSize = fieldDecl.Type.Handle.SizeOf;

            if (unitSize is not (1 or 2 or 4 or 8))
            {
                Diagnostic.Log(DiagnosticLevel.Warning, $"Bit-field '{fieldDecl.Name}' in '{recordName}' has an unsupported storage unit size of {unitSize} bytes; assuming 8.");
                unitSize = 8;
            }

            // Gather the run of consecutive bit-fields that fit in the storage window of the first one.
            // Clang reports the bit offsets, so the ABI packing rules don't need to be re-derived here.
            long windowEndBit = (fieldDecl.Handle.OffsetOfField / 8) * 8 + unitSize * 8;

            List<FieldDecl> run = new();

            while (i < fieldDecls.Count
                && fieldDecls[i].IsBitField
                && fieldDecls[i].BitWidthValue > 0
                && fieldDecls[i].Handle.OffsetOfField + fieldDecls[i].BitWidthValue <= windowEndBit)
            {
                run.Add(fieldDecls[i]);
                i++;
            }

            members.AddRange(GenerateBitfieldRun(run, bitfieldRunCount++));
        }

        foreach (IndirectFieldDecl indirectFieldDecl in indirectFieldDecls)
        {
            IDeclContext declContext = indirectFieldDecl.AnonField.DeclContext!;

            if (declContext is RecordDecl contextRecordDecl)
            {
                string typeName = GetTypeFullName(indirectFieldDecl.Type);
                string declContextName = GetTypeFullName(contextRecordDecl.TypeForDecl).Replace('.', '_') + "_Field";
                string fieldName = GetValidIdentifier(indirectFieldDecl.Name);

                members.Add(new KeyValuePair<string, string>(fieldName, $@"
                    public ref {typeName} {fieldName} => ref {declContextName}.{fieldName};
                "));
            }
        }

        string[] outerTypes = GetTypeFullName(recordDecl.TypeForDecl).Split('.')[..^1];

        string layoutAttribute = hasBitfields
            ? $"[StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = {recordSize})]"
            : recordDecl.IsUnion
                ? "[StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]"
                : string.Empty;

        string header = $"{string.Concat(outerTypes.Select(outerType => $"public partial struct {outerType}{{\n"))}{layoutAttribute}\npublic {(hasBitfields ? "unsafe " : string.Empty)}partial struct {recordName}\n{{";
        string footer = new string('}', outerTypes.Length + 1);

        string text = $"{header}\n{string.Join("\n", members.Select(member => member.Value))}\n{footer}";

        return new Declaration
        {
            Kind = "Record",
            Name = recordName,
            Text = text,
            Header = header,
            Footer = footer,
            Members = members,
        };
    }

    private static string GenerateFieldMemberName(FieldDecl fieldDecl)
    {
        string fieldName = GetValidIdentifier(fieldDecl.Name);

        if (fieldDecl.IsAnonymousField)
            fieldName = GetTypeFullName(fieldDecl.Type).Replace('.', '_') + "_Field";

        return fieldName;
    }

    private static string GenerateFieldDecl(RecordDecl recordDecl, FieldDecl fieldDecl, bool explicitLayout)
    {
        if (fieldDecl.Type is IncompleteArrayType)
        {
            // A trailing flexible array member is excluded from sizeof in C, so omitting it keeps the layout correct.
            Diagnostic.Log(DiagnosticLevel.Warning, $"Flexible array member '{fieldDecl.Name}' in '{recordDecl.Name}' is not emitted; sizeof matches C but the trailing elements are not accessible.");
            return $"// Flexible array member '{fieldDecl.Name}' is not emitted.";
        }

        if (IsType<BuiltinType>(fieldDecl.Type, out BuiltinType? builtinType) && builtinType.Kind == CXTypeKind.CXType_LongDouble)
            Diagnostic.Log(DiagnosticLevel.Warning, $"'long double' field '{fieldDecl.Name}' in '{recordDecl.Name}' is emitted as 'decimal'; the size matches on this ABI but the value semantics do not.");

        string fieldName = GenerateFieldMemberName(fieldDecl);
        string typeName = GetTypeFullName(fieldDecl.Type);
        string layout = explicitLayout
            ? $"[System.Runtime.InteropServices.FieldOffset({fieldDecl.Handle.OffsetOfField / 8})]\n"
            : recordDecl.IsUnion
                ? "[System.Runtime.InteropServices.FieldOffset(0)]\n"
                : string.Empty;

        return $"{layout}public {typeName} {fieldName};";
    }

    // A run of consecutive bit-fields is stored in a single fixed byte buffer; the buffer is accessed
    // byte-wise with a read-modify-write, so only this run's bits are ever touched, even when the run
    // starts at an unaligned offset or shares bytes with a neighbouring field.
    private static IEnumerable<KeyValuePair<string, string>> GenerateBitfieldRun(List<FieldDecl> run, int runIndex)
    {
        long startBit = run[0].Handle.OffsetOfField;
        long startByte = startBit / 8;
        long endByte = (run[^1].Handle.OffsetOfField + run[^1].BitWidthValue + 7) / 8;
        long length = endByte - startByte;

        string backingFieldName = $"__bits{runIndex}";

        yield return new KeyValuePair<string, string>(backingFieldName, $"[System.Runtime.InteropServices.FieldOffset({startByte})]\nprivate fixed byte {backingFieldName}[{length}];");

        foreach (FieldDecl fieldDecl in run)
        {
            if (fieldDecl.IsUnnamedBitfield)
                continue;

            yield return new KeyValuePair<string, string>(GetValidIdentifier(fieldDecl.Name), GenerateBitfieldProperty(fieldDecl, backingFieldName, startBit, length));
        }
    }

    private static string GenerateBitfieldProperty(FieldDecl fieldDecl, string backingFieldName, long startBit, long length)
    {
        long bitOffset = fieldDecl.Handle.OffsetOfField;
        int bitWidth = fieldDecl.BitWidthValue;
        long shift = bitOffset - startBit;

        // A 64-bit mask cannot be written as (1ul << 64) - 1 because the shift count is masked to 6 bits.
        ulong mask = bitWidth == 64 ? ulong.MaxValue : (1ul << bitWidth) - 1;
        ulong shiftedMask = mask << (int)shift;

        string propertyName = GetValidIdentifier(fieldDecl.Name);
        string propertyType = GetTypeName(fieldDecl.Type);

        // _Bool maps to byte without DisableRuntimeMarshalling, so the plain integer path handles that case.
        bool isBool = propertyType == "bool";
        bool signed = !isBool && IsSignedBitfieldType(fieldDecl.Type);

        // Pack/unpack the backing buffer byte-wise so runs at any bit offset work; this assumes little-endian.
        string maskLiteral = $"0x{mask:X}ul";
        string shiftedMaskLiteral = $"0x{shiftedMask:X}ul";

        string packExpression = $"({string.Join(" | ", Enumerable.Range(0, (int)length).Select(index => $"((ulong){backingFieldName}[{index}] << {8 * index})"))})";

        string[] unpackStatements = Enumerable.Range(0, (int)length)
            .Select(index => $"{backingFieldName}[{index}] = (byte)(__v >> {8 * index});")
            .ToArray();

        string valueExpression = isBool ? "(ulong)(value ? 1 : 0)" : "(ulong)value";

        string getExpression;
        if (isBool)
            getExpression = $"(({packExpression} >> {shift}) & {maskLiteral}) != 0";
        else if (signed)
            // Sign-extend by moving the field's top bit to bit 63 of a long and arithmetic-shifting back down.
            getExpression = $"({propertyType})((long)({packExpression} << {64 - shift - bitWidth}) >> {64 - bitWidth})";
        else
            getExpression = $"({propertyType})(({packExpression} >> {shift}) & {maskLiteral})";

        return @$"
            public {propertyType} {propertyName}
            {{
                get => {getExpression};
                set
                {{
                    ulong __v = {packExpression};
                    __v = (__v & ~{shiftedMaskLiteral}) | (({valueExpression} & {maskLiteral}) << {shift});
                    {string.Join("\n", unpackStatements)}
                }}
            }}
        ";
    }

    private static bool IsSignedBitfieldType(Type type)
    {
        type = type.CanonicalType;

        if (type is BuiltinType builtinType)
            // The Kind set matches GetTypeName's signed builtin kinds; libClangSharp's CXType.IsSigned is unreliable.
            return builtinType.Kind is CXTypeKind.CXType_SChar or CXTypeKind.CXType_Short or CXTypeKind.CXType_Int or CXTypeKind.CXType_Long or CXTypeKind.CXType_LongLong;

        if (type is EnumType enumType)
            return IsSignedBitfieldType(enumType.Decl.IntegerType);

        return false;
    }

    private static string GenerateRecordEqualityMethods(Type record)
    {
        string recordName = GetTypeName(record);

        return GenerateOuterDeclarations(record, $$"""
            public partial struct {{recordName}} : IEquatable<{{recordName}}>
            {
                {{GenerateRecordEqualityFunctions(recordName)}}
            }
            """);
    }

    private static string GenerateEnumDecl(EnumDecl enumDecl)
    {
        List<string> enumMembers = new();
        bool hasNegatives = false;

        foreach (EnumConstantDecl enumConstant in enumDecl.Enumerators)
        {
            // Signedness follows the GCC/Clang rule instead of the host ABI (MSVC always uses int), so output is host-independent.
            if (enumConstant.IsNegative)
                hasNegatives = true;

            enumMembers.Add(GenerateEnumConstantDecl(enumConstant));
        }

        return GenerateOuterDeclarations(enumDecl.TypeForDecl, $@"
            public enum {GetTypeName(enumDecl.TypeForDecl)} : {GetIntegerName(enumDecl.IntegerType.Handle.SizeOf, hasNegatives, "INVALID_ENUM_INTEGER")}
            {{ 
                {string.Join(",\n", enumMembers)}
            }}
        ");
    }

    private static string GenerateEnumConstantDecl(EnumConstantDecl enumConstantDecl)
    {
        string value = enumConstantDecl.IsSigned
            ? enumConstantDecl.InitVal.ToString(CultureInfo.InvariantCulture)
            : enumConstantDecl.UnsignedInitVal.ToString(CultureInfo.InvariantCulture);

        return $"{GetValidIdentifier(enumConstantDecl.Name)} = {value}";
    }

    private static string GenerateEnumDeclConstants(EnumDecl enumDecl)
    {
        string enumName = GetTypeFullName(enumDecl.TypeForDecl);

        IEnumerable<string> constantFields = enumDecl.Decls.OfType<EnumConstantDecl>().Select(enumConstantDecl =>
        {
            string enumMemberName = GetValidIdentifier(enumConstantDecl.Name);
            return $"public const {enumName} {enumMemberName} = {enumName}.{enumMemberName};";
        });

        return string.Join("\n", constantFields);
    }

    private static string GenerateMacroVarDecl(VarDecl varDecl)
    {
        if (!varDecl.Name.StartsWith(MacroPrefix, StringComparison.Ordinal))
            return string.Empty;

        string macroName = varDecl.Name[MacroPrefix.Length..];

        if (!varDecl.HasInit)
        {
            Diagnostic.Log(DiagnosticLevel.Info, $"Skipped macro '{macroName}': it has no value.");
            return string.Empty;
        }

        Expr init = varDecl.Init;
        CXEvalResult result = varDecl.Handle.Evaluate;

        string typeName = GetTypeFullName(varDecl.Type);
        string? expression = null;

        try
        {
            switch (result.Kind)
            {
                case CXEvalResultKind.CXEval_Float:
                    expression = init.Type.Kind switch
                    {
                        CXTypeKind.CXType_Double =>  result.AsDouble.ToString(CultureInfo.InvariantCulture),
                        CXTypeKind.CXType_Float =>  ((float)result.AsDouble).ToString(CultureInfo.InvariantCulture) + "f",
                        CXTypeKind.CXType_LongDouble =>  ((decimal)result.AsDouble).ToString(CultureInfo.InvariantCulture),
                        _ => $"INVALID_FLOAT_{init.Type.Kind}"
                    };
                    break;
                case CXEvalResultKind.CXEval_Int:
                    expression = init.Type.Handle.SizeOf switch
                    {
                        1 => result.IsUnsignedInt ? ((byte)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((sbyte)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                        2 => result.IsUnsignedInt ? ((ushort)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((short)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                        4 => result.IsUnsignedInt ? ((uint)result.AsUnsigned).ToString(CultureInfo.InvariantCulture) : ((int)result.AsLongLong).ToString(CultureInfo.InvariantCulture),
                        8 => result.IsUnsignedInt ? result.AsUnsigned.ToString(CultureInfo.InvariantCulture) : result.AsLongLong.ToString(CultureInfo.InvariantCulture),
                        _ => $"INVALID_INTEGER_SIZEOF_{init.Type.Handle.SizeOf}"
                    };
                    break;
                case CXEvalResultKind.CXEval_StrLiteral:
                    typeName = "string";
                    expression = result.AsStr is null ? null : "\"" + result.AsStr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                    break;
                case CXEvalResultKind.CXEval_ObjCStrLiteral:
                case CXEvalResultKind.CXEval_CFStr:
                case CXEvalResultKind.CXEval_Other:
                case CXEvalResultKind.CXEval_UnExposed:
                default:
                    Diagnostic.Log(DiagnosticLevel.Info, $"Skipped macro '{macroName}': its value could not be evaluated.");
                    break;
            }
        }
        finally
        {
            result.Dispose();
        }

        return string.IsNullOrEmpty(expression)
            ? string.Empty
            : $"public const {typeName} {GetValidIdentifier(macroName)} = {expression};";
    }

    private static string GenerateExternVarDeclNativeVariable(VarDecl varDecl)
    {
        return $$"""
            extern void* {{varDecl.Name}};
            """;
    }

    private static string GenerateExternVarDeclNativeGetter(VarDecl varDecl)
    {
        return $$"""
        BINDGEN_API void* {{GenerateExternGetterName(varDecl.Name)}}() {
            return &{{varDecl.Name}};
        }
        """;
    }

    private static string GenerateExternVarDeclManagedGetter(VarDecl varDecl)
    {
        string validName = GetValidIdentifier(varDecl.Name);
        return $$"""
            [DllImport(BindgenInternal.DllImportPath, EntryPoint = "{{GenerateExternGetterName(varDecl.Name)}}")]
            private static extern void* {{GenerateExternGetterName(validName)}}();
            """;
    }

    private static string GenerateExternVarDeclField(VarDecl varDecl)
    {
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);
        return varDecl.HasExternalStorage ? $"private static void* {fieldName};" : "";
    }

    private static string GenerateExternVarDeclProperty(VarDecl varDecl)
    {
        if (!varDecl.HasExternalStorage)
            return "";

        string typeName = GetTypeFullName(varDecl.Type);
        string validName = GetValidIdentifier(varDecl.Name);
        string fieldName = GenerateExternFieldName(validName);
        string getterName = GenerateExternGetterName(validName);

        // We can't use Unsafe.AsRef<T>(void*) because T can't be a pointer.
        return $"public static ref {typeName} {validName} => ref *({typeName}*)({fieldName} == null ? {fieldName} = {getterName}() : {fieldName});";
    }

    // This converts value-like macros to type-inferred variables so we can get access to it's type information.
    // The macro's constants will be generated in GenerateMacroVarDecl().
    private static string GenerateMacroDummy(MacroDefinitionRecord macro)
    {
        if (macro.IsFunctionLike)
            return string.Empty;

        CXTranslationUnit translationUnitHandle = macro.TranslationUnit.Handle;
        Span<CXToken> tokens = translationUnitHandle.Tokenize(macro.Extent);

        bool hasNoValue = tokens[0].Kind != CXTokenKind.CXToken_Identifier ||
                          tokens[0].GetSpelling(translationUnitHandle).CString != macro.Spelling ||
                          tokens.Length == 1;

        if (hasNoValue)
            return string.Empty;

        CXSourceLocation sourceRangeEnd = tokens[^1].GetExtent(translationUnitHandle).End;
        CXSourceLocation sourceRangeStart = tokens[1].GetLocation(translationUnitHandle);
        CXSourceRange sourceRange = CXSourceRange.Create(sourceRangeStart, sourceRangeEnd);

        string value = GetSourceRangeContents(translationUnitHandle, sourceRange);

        return $"const __auto_type {MacroPrefix}{macro.Name} = {value};";
    }

    /// <summary>
    /// Returns <see cref="GetTypeName"/> mapped to a valid C# identifier by
    /// replacing '*' with 'P' and every other non-identifier character with
    /// '_', so array-of-function-pointer types (e.g. "delegate* unmanaged&lt;...&gt;")
    /// yield a usable struct name.
    /// </summary>
    private static string GetTypeIdentifier(Type type)
    {
        string name = GetTypeName(type);

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c == '*' ? 'P' : char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        return sb.ToString();
    }

    private static string GenerateRecordEqualityFunctions(string recordName)
    {
        return $$"""
            public bool Equals({{recordName}} other)
            {
                fixed ({{recordName}}* __self = &this)
                {
                    return new Span<byte>(__self, sizeof({{recordName}})).SequenceEqual(new Span<byte>(&other, sizeof({{recordName}})));
                }
            }

            public override bool Equals(object? obj)
            {
                return obj is {{recordName}} other && Equals(other);
            }

            public static bool operator ==({{recordName}} left, {{recordName}} right)
            {
                return left.Equals(right);
            }

            public static bool operator !=({{recordName}} left, {{recordName}} right)
            {
                return !(left == right);
            }

            public override int GetHashCode()
            {
                fixed ({{recordName}}* __self = &this)
                {
                    HashCode hash = new();
                    hash.AddBytes(new Span<byte>(__self, sizeof({{recordName}})));
                    return hash.ToHashCode();
                }
            }
            """;
    }
}
