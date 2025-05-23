// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module internal FSharp.Compiler.Lexhelp

open System
open System.Text

open Internal.Utilities
open Internal.Utilities.Library
open Internal.Utilities.Text.Lexing

open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.IO
open FSharp.Compiler.ParseHelpers
open FSharp.Compiler.Parser
open FSharp.Compiler.Syntax
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.UnicodeLexing

/// The "mock" file name used by fsi.exe when reading from stdin.
/// Has special treatment by the lexer, i.e. __SOURCE_DIRECTORY__ becomes GetCurrentDirectory()
let stdinMockFileName = "stdin"

/// Lexer args: status of #light processing.  Mutated when a #light
/// directive is processed. This alters the behaviour of the lexfilter.
[<Sealed>]
type IndentationAwareSyntaxStatus(initial: bool, warn: bool) =
    let mutable status = None

    member x.Status
        with get () =
            match status with
            | None -> initial
            | Some v -> v
        and set v = status <- Some(v)

    member x.ExplicitlySet = status.IsSome
    member x.WarnOnMultipleTokens = warn

/// Manage lexer resources (string interning)
[<Sealed>]
type LexResourceManager(?capacity: int) =
    let strings =
        System.Collections.Concurrent.ConcurrentDictionary<string, token>(Environment.ProcessorCount, defaultArg capacity 1024)

    member x.InternIdentifierToken(s) =
        match strings.TryGetValue s with
        | true, res -> res
        | _ ->
            let res = IDENT s
            strings[s] <- res
            res

/// Lexer parameters
type LexArgs =
    {
        conditionalDefines: string list
        resourceManager: LexResourceManager
        diagnosticsLogger: DiagnosticsLogger
        applyLineDirectives: bool
        pathMap: PathMap
        mutable ifdefStack: LexerIfdefStack
        mutable indentationSyntaxStatus: IndentationAwareSyntaxStatus
        mutable stringNest: LexerInterpolatedStringNesting
        mutable interpolationDelimiterLength: int
    }

/// possible results of lexing a long Unicode escape sequence in a string literal, e.g. "\U0001F47D",
/// "\U000000E7", or "\UDEADBEEF" returning SurrogatePair, SingleChar, or Invalid, respectively
type LongUnicodeLexResult =
    | SurrogatePair of uint16 * uint16
    | SingleChar of uint16
    | Invalid

let mkLexargs
    (conditionalDefines, indentationSyntaxStatus, resourceManager, ifdefStack, diagnosticsLogger, pathMap: PathMap, applyLineDirectives)
    =
    {
        conditionalDefines = conditionalDefines
        ifdefStack = ifdefStack
        indentationSyntaxStatus = indentationSyntaxStatus
        resourceManager = resourceManager
        diagnosticsLogger = diagnosticsLogger
        applyLineDirectives = applyLineDirectives
        stringNest = []
        pathMap = pathMap
        interpolationDelimiterLength = 0
    }

/// Register the lexbuf and call the given function
let reusingLexbufForParsing (lexbuf: Lexbuf) f =
    use _ = UseBuildPhase BuildPhase.Parse

    try
        f ()
    with
    | :? OperationCanceledException -> reraise ()
    | e ->
        raise (
            WrappedError(
                e,
                (try
                    lexbuf.LexemeRange
                 with _ ->
                     range0)
            )
        )

let resetLexbufPos fileName (lexbuf: Lexbuf) =
    lexbuf.EndPos <- Position.FirstLine(FileIndex.fileIndexOfFile fileName)

/// Reset the lexbuf, configure the initial position with the given file name and call the given function
let usingLexbufForParsing (lexbuf: Lexbuf, fileName) f =
    resetLexbufPos fileName lexbuf
    reusingLexbufForParsing lexbuf (fun () -> f lexbuf)

//------------------------------------------------------------------------
// Functions to manipulate lexer transient state
//-----------------------------------------------------------------------

let stringBufferAsString (buf: ByteBuffer) =
    let buf = buf.AsMemory()

    if buf.Length % 2 <> 0 then
        failwith "Expected even number of bytes"

    let chars: char[] = Array.zeroCreate (buf.Length / 2)

    for i = 0 to (buf.Length / 2) - 1 do
        let hi = buf.Span[i * 2 + 1]
        let lo = buf.Span[i * 2]
        let c = char (((int hi) * 256) + (int lo))
        chars[i] <- c

    String(chars)

/// When lexing bytearrays we don't expect to see any unicode stuff.
/// Likewise when lexing string constants we shouldn't see any trigraphs > 127
/// So to turn the bytes collected in the string buffer back into a bytearray
/// we just take every second byte we stored.  Note all bytes > 127 should have been
/// stored using addIntChar
let stringBufferAsBytes (buf: ByteBuffer) =
    let bytes = buf.AsMemory()
    Array.init (bytes.Length / 2) (fun i -> bytes.Span[i * 2])

[<Flags>]
type LexerStringFinisherContext =
    | InterpolatedPart = 1
    | Verbatim = 2
    | TripleQuote = 4

type LexerStringFinisher =
    | LexerStringFinisher of (ByteBuffer -> LexerStringKind -> LexerStringFinisherContext -> LexerContinuation -> token)

    member fin.Finish (buf: ByteBuffer) kind context cont =
        let (LexerStringFinisher f) = fin
        f buf kind context cont

    static member Default =
        LexerStringFinisher(fun buf kind context cont ->
            let isPart = context.HasFlag(LexerStringFinisherContext.InterpolatedPart)
            let isVerbatim = context.HasFlag(LexerStringFinisherContext.Verbatim)
            let isTripleQuote = context.HasFlag(LexerStringFinisherContext.TripleQuote)

            if kind.IsInterpolated then
                let s = stringBufferAsString buf

                if kind.IsInterpolatedFirst then
                    let synStringKind =
                        if isTripleQuote then SynStringKind.TripleQuote
                        elif isVerbatim then SynStringKind.Verbatim
                        else SynStringKind.Regular

                    if isPart then
                        INTERP_STRING_BEGIN_PART(s, synStringKind, cont)
                    else
                        INTERP_STRING_BEGIN_END(s, synStringKind, cont)
                else if isPart then
                    INTERP_STRING_PART(s, cont)
                else
                    INTERP_STRING_END(s, cont)
            elif kind.IsByteString then
                let synByteStringKind =
                    if isVerbatim then
                        SynByteStringKind.Verbatim
                    else
                        SynByteStringKind.Regular

                BYTEARRAY(stringBufferAsBytes buf, synByteStringKind, cont)
            else
                let synStringKind =
                    if isVerbatim then SynStringKind.Verbatim
                    elif isTripleQuote then SynStringKind.TripleQuote
                    else SynStringKind.Regular

                STRING(stringBufferAsString buf, synStringKind, cont))

type LexerStringArgs = ByteBuffer * LexerStringFinisher * range * LexerStringKind * LexArgs
type SingleLineCommentArgs = (range * StringBuilder) option * int * range * range * LexArgs
type BlockCommentArgs = int * range * LexArgs

let addUnicodeString (buf: ByteBuffer) (x: string) =
    buf.EmitBytes(Encoding.Unicode.GetBytes x)

let addIntChar (buf: ByteBuffer) c =
    buf.EmitIntAsByte(c % 256)
    buf.EmitIntAsByte(c / 256)

let addUnicodeChar buf c = addIntChar buf (int c)

let addByteChar buf (c: char) = addIntChar buf (int32 c % 256)

type LargerThanOneByte = int
type LargerThan127ButInsideByte = int

/// Sanity check that high bytes are zeros. Further check each low byte <= 127
let errorsInByteStringBuffer (buf: ByteBuffer) =
    let bytes = buf.AsMemory()
    assert (bytes.Length % 2 = 0)

    // Enhancement?: return faulty values?
    //     But issue: we don't know range of values -> no direct mapping from value to range & notation

    // values with high byte <> 0
    let mutable largerThanOneByteCount = 0
    // values with high byte = 0, but low byte > 127
    let mutable largerThan127ButSingleByteCount = 0

    for i = 0 to bytes.Length / 2 - 1 do
        if bytes.Span[i * 2 + 1] <> 0uy then
            largerThanOneByteCount <- largerThanOneByteCount + 1
        elif bytes.Span[i * 2] > 127uy then
            largerThan127ButSingleByteCount <- largerThan127ButSingleByteCount + 1

    if largerThanOneByteCount + largerThan127ButSingleByteCount > 0 then
        Some(largerThanOneByteCount, largerThan127ButSingleByteCount)
    else
        None

let incrLine (lexbuf: LexBuffer<_>) = lexbuf.EndPos <- lexbuf.EndPos.NextLine

let advanceColumnBy (lexbuf: LexBuffer<_>) n =
    lexbuf.EndPos <- lexbuf.EndPos.ShiftColumnBy(n)

let trigraph c1 c2 c3 =
    let digit (c: char) = int c - int '0'
    char (digit c1 * 100 + digit c2 * 10 + digit c3)

let digit d =
    if d >= '0' && d <= '9' then
        int32 d - int32 '0'
    else
        failwith "digit"

let hexdigit d =
    if d >= '0' && d <= '9' then digit d
    elif d >= 'a' && d <= 'f' then int32 d - int32 'a' + 10
    elif d >= 'A' && d <= 'F' then int32 d - int32 'A' + 10
    else failwith "hexdigit"

let unicodeGraphShort (s: string) =
    if s.Length <> 4 then
        failwith "unicodegraph"

    uint16 (hexdigit s[0] * 4096 + hexdigit s[1] * 256 + hexdigit s[2] * 16 + hexdigit s[3])

let hexGraphShort (s: string) =
    if s.Length <> 2 then
        failwith "hexgraph"

    uint16 (hexdigit s[0] * 16 + hexdigit s[1])

let unicodeGraphLong (s: string) =
    if s.Length <> 8 then
        failwith "unicodeGraphLong"

    let high =
        hexdigit s[0] * 4096 + hexdigit s[1] * 256 + hexdigit s[2] * 16 + hexdigit s[3] in

    let low =
        hexdigit s[4] * 4096 + hexdigit s[5] * 256 + hexdigit s[6] * 16 + hexdigit s[7] in
    // not a surrogate pair
    if high = 0 then
        SingleChar(uint16 low)
    // invalid encoding
    elif high > 0x10 then
        Invalid
    // valid supplementary character: code points U+10000 to U+10FFFF
    // valid surrogate pair: see http://www.unicode.org/versions/latest/ch03.pdf , "Surrogates" section
    // high-surrogate code point (U+D800 to U+DBFF) followed by low-surrogate code point (U+DC00 to U+DFFF)
    else
        let codepoint = high * 0x10000 + low
        let hiSurr = uint16 (0xD800 + ((codepoint - 0x10000) / 0x400))
        let loSurr = uint16 (0xDC00 + ((codepoint - 0x10000) % 0x400))
        SurrogatePair(hiSurr, loSurr)

let escape c =
    match c with
    | '\\' -> '\\'
    | '\'' -> '\''
    | 'a' -> char 7
    | 'f' -> char 12
    | 'v' -> char 11
    | 'n' -> '\n'
    | 't' -> '\t'
    | 'b' -> '\b'
    | 'r' -> '\r'
    | c -> c

//------------------------------------------------------------------------
// Keyword table
//-----------------------------------------------------------------------

exception ReservedKeyword of string * range

module Keywords =
    type private compatibilityMode =
        | ALWAYS (* keyword *)
        | FSHARP (* keyword, but an identifier under --ml-compatibility mode *)

    let private keywordList =
        [
            FSHARP, "abstract", ABSTRACT
            ALWAYS, "and", AND
            ALWAYS, "as", AS
            ALWAYS, "assert", ASSERT
            ALWAYS, "asr", INFIX_STAR_STAR_OP "asr"
            ALWAYS, "base", BASE
            ALWAYS, "begin", BEGIN
            ALWAYS, "class", CLASS
            FSHARP, "const", CONST
            FSHARP, "default", DEFAULT
            FSHARP, "delegate", DELEGATE
            ALWAYS, "do", DO
            ALWAYS, "done", DONE
            FSHARP, "downcast", DOWNCAST
            ALWAYS, "downto", DOWNTO
            FSHARP, "elif", ELIF
            ALWAYS, "else", ELSE
            ALWAYS, "end", END
            ALWAYS, "exception", EXCEPTION
            FSHARP, "extern", EXTERN
            ALWAYS, "false", FALSE
            ALWAYS, "finally", FINALLY
            FSHARP, "fixed", FIXED
            ALWAYS, "for", FOR
            ALWAYS, "fun", FUN
            ALWAYS, "function", FUNCTION
            FSHARP, "global", GLOBAL
            ALWAYS, "if", IF
            ALWAYS, "in", IN
            ALWAYS, "inherit", INHERIT
            FSHARP, "inline", INLINE
            FSHARP, "interface", INTERFACE
            FSHARP, "internal", INTERNAL
            ALWAYS, "land", INFIX_STAR_DIV_MOD_OP "land"
            ALWAYS, "lazy", LAZY
            ALWAYS, "let", LET(false)
            ALWAYS, "lor", INFIX_STAR_DIV_MOD_OP "lor"
            ALWAYS, "lsl", INFIX_STAR_STAR_OP "lsl"
            ALWAYS, "lsr", INFIX_STAR_STAR_OP "lsr"
            ALWAYS, "lxor", INFIX_STAR_DIV_MOD_OP "lxor"
            ALWAYS, "match", MATCH
            FSHARP, "member", MEMBER
            ALWAYS, "mod", INFIX_STAR_DIV_MOD_OP "mod"
            ALWAYS, "module", MODULE
            ALWAYS, "mutable", MUTABLE
            FSHARP, "namespace", NAMESPACE
            ALWAYS, "new", NEW
            FSHARP, "null", NULL
            ALWAYS, "of", OF
            ALWAYS, "open", OPEN
            ALWAYS, "or", OR
            FSHARP, "override", OVERRIDE
            ALWAYS, "private", PRIVATE
            FSHARP, "public", PUBLIC
            ALWAYS, "rec", REC
            FSHARP, "return", YIELD(false)
            ALWAYS, "sig", SIG
            FSHARP, "static", STATIC
            ALWAYS, "struct", STRUCT
            ALWAYS, "then", THEN
            ALWAYS, "to", TO
            ALWAYS, "true", TRUE
            ALWAYS, "try", TRY
            ALWAYS, "type", TYPE
            FSHARP, "upcast", UPCAST
            FSHARP, "use", LET(true)
            ALWAYS, "val", VAL
            FSHARP, "void", VOID
            ALWAYS, "when", WHEN
            ALWAYS, "while", WHILE
            ALWAYS, "with", WITH
            FSHARP, "yield", YIELD(true)
            ALWAYS, "_", UNDERSCORE
            (*------- for prototyping and explaining offside rule *)
            FSHARP, "__token_OBLOCKSEP", OBLOCKSEP
            FSHARP, "__token_OWITH", OWITH
            FSHARP, "__token_ODECLEND", ODECLEND range0
            FSHARP, "__token_OTHEN", OTHEN
            FSHARP, "__token_OELSE", OELSE
            FSHARP, "__token_OEND", OEND
            FSHARP, "__token_ODO", ODO
            FSHARP, "__token_OLET", OLET(true)
            FSHARP, "__token_constraint", CONSTRAINT
        ]
        (*------- reserved keywords which are ml-compatibility ids *)
        @ List.map
            (fun s -> (FSHARP, s, RESERVED))
            [
                "break"
                "checked"
                "component"
                "constraint"
                "continue"
                "fori"
                "include"
                "mixin"
                "parallel"
                "params"
                "process"
                "protected"
                "pure"
                "sealed"
                "trait"
                "tailcall"
                "virtual"
            ]

    //------------------------------------------------------------------------
    // Keywords
    //-----------------------------------------------------------------------

    let keywordNames = keywordList |> List.map (fun (_, w, _) -> w)

    let keywordTable =
        let tab = System.Collections.Generic.Dictionary<string, token>(100)

        for _, keyword, token in keywordList do
            tab.Add(keyword, token)

        tab

    let IdentifierToken args (lexbuf: Lexbuf) (s: string) =
        if IsCompilerGeneratedName s then
            warning (Error(FSComp.SR.lexhlpIdentifiersContainingAtSymbolReserved (), lexbuf.LexemeRange))

        args.resourceManager.InternIdentifierToken s

    let KeywordOrIdentifierToken args (lexbuf: Lexbuf) s =
        match keywordTable.TryGetValue s with
        | true, v ->
            match v with
            | RESERVED ->
                warning (ReservedKeyword(FSComp.SR.lexhlpIdentifierReserved (s), lexbuf.LexemeRange))
                IdentifierToken args lexbuf s
            | _ ->
                match s with
                | "land"
                | "lor"
                | "lxor"
                | "lsl"
                | "lsr"
                | "asr" ->
                    if lexbuf.SupportsFeature LanguageFeature.MLCompatRevisions then
                        mlCompatWarning (FSComp.SR.mlCompatKeyword (s)) lexbuf.LexemeRange
                | _ -> ()

                v
        | _ ->
            match s with
            | "__SOURCE_DIRECTORY__" ->
                let fileName = FileIndex.fileOfFileIndex lexbuf.StartPos.FileIndex

                let dirname =
                    if String.IsNullOrWhiteSpace(fileName) then
                        String.Empty
                    else if fileName = stdinMockFileName then
                        System.IO.Directory.GetCurrentDirectory()
                    else
                        fileName
                        |> FileSystem.GetFullPathShim (* asserts that path is already absolute *)
                        |> System.IO.Path.GetDirectoryName
                        |> (!!)

                if String.IsNullOrEmpty dirname then
                    dirname
                else
                    PathMap.applyDir args.pathMap dirname
                |> fun dir -> KEYWORD_STRING(s, dir)
            | "__SOURCE_FILE__" -> KEYWORD_STRING(s, !!System.IO.Path.GetFileName(FileIndex.fileOfFileIndex lexbuf.StartPos.FileIndex))
            | "__LINE__" -> KEYWORD_STRING(s, string lexbuf.StartPos.Line)
            | _ -> IdentifierToken args lexbuf s

/// Arbitrary value
[<Literal>]
let StringCapacity = 100
