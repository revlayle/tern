# 13. Grammar

[Guide index](README.md)

Tern's whole syntax in one place, for reference: the spec's sections 2 to 9 collected as a grammar. It describes the language as built; planned forms are listed in 13.7. The grammar gives the shape of a program only: types, constant rules, where each declaration may appear and what each form means are in the spec sections it names (`docs/spec-v0.md`). Where the grammar and the spec disagree, one of them is wrong, and the difference is fixed in both.

**Notation** (EBNF): `Rule = ...` defines a rule; `"x"` is the text `x`; `|` separates choices; `[ x ]` is optional; `{ x }` repeats zero or more times; `( x )` groups; `(* ... *)` is a comment. Rules in capitals (`IDENT`, `NUMBER`, `NL`, `END`) are tokens, defined in 13.1.

## 13.1 Tokens

```
IDENT    = ( letter | "_" ) { letter | digit | "_" }       (* ASCII only, case-sensitive (spec 2) *)
letter   = "A".."Z" | "a".."z"
digit    = "0".."9"
hexdigit = digit | "A".."F" | "a".."f"

NUMBER   = digit { digit | "_" }                           (* decimal: 200 *)
         | "$" hexdigit { hexdigit | "_" }                 (* hex: $C000 *)
         | ( "0b" | "0B" ) { "0" | "1" | "_" }             (* binary: 0b1010_0101; at least one digit *)
REAL     = digit { digit | "_" } ( "." digit { digit | "_" } [ exponent ] | exponent )
exponent = ( "e" | "E" ) [ "+" | "-" ] digit { digit }     (* 1.5, 2e3; compile-time code only (spec 4.1) *)

CHAR     = [ prefix ] "'" ( char | escape ) "'"
STRING   = [ prefix ] '"' { char | escape } '"'            (* on one line *)
prefix   = IDENT [ "." IDENT ]                             (* an encoding, right before the quote: screen"HI", font.tiles'A' (spec 2.1) *)
escape   = "\0" | "\n" | "\\" | '\"' | "\'" | "\$" hexdigit hexdigit
```

- A number has no sign: `-5` is unary `-` and `5` (spec 2.1). `0..25` is a range, not a `REAL`.
- **Comments** are `// to the end of the line` and `/* block */`; they are not tokens. A line break inside a block comment counts as one line break.
- **Line breaks.** `NL` is a line break that ends a statement. A line break is an `NL` only when both are true:
  - the token before it can end a statement: an `IDENT`, `NUMBER`, `REAL`, `CHAR` or `STRING`, `)`, `]`, `}`, `true`, `false`, `zp`, `return`, `break` or `continue`;
  - it is not inside `( )` or `[ ]` opened since the last `{` or `}`. A `{` or `}` ends that rule, so inside a struct literal in a list, a line break separates fields again (spec 2, 3.3).

  Any other line break is a space. A line that ends with a binary operator, `,`, `=` or `(` therefore continues on the next line, and a line that starts with a binary operator is an error (spec 2). In a `memory` block, a line that ends with the kind `volatile` ends there too.
- **Statement ends.** `END` is an `NL`, a `;`, or nothing before the `}` that closes the enclosing block (or the end of the file). Wherever a list of statements or declarations is read, extra `NL`s and `;`s are skipped, so an empty statement (`;;`) is ignored.
- Where a block follows a header (`fn`, `if`, `else`, `while`, `for`, `match`, `struct`, `encoding`, `asm`, `emit`, `memory`), `NL`s before its `{` are skipped, and an `else` may start the line after the `}` (spec 2).

## 13.2 Keywords and words

**Keywords** are reserved everywhere and are never names:

```
as         asm        assert     break      const      continue   data       else
export     false      fn         for        if         implements import     in
inline     interrupt  keeps      linear     match      module     noinit     private
return     struct     true       using      var        volatile   while      zp
```

**Words with a meaning in one place** are names everywhere else (spec 13):

| Word | Meaning only where |
|---|---|
| `step` | right after the range of a `for`, or of a list built with `for` |
| `emit` | at the start of a statement in a `const fn`, before `{` |
| `indexed` | before `var` or `match` (also after `noinit`, `volatile`, `private`) |
| `align` | right after `@` |
| `ptr` | at the start of a type, before a type |
| `encoding`, `default` | at the start of a top-level declaration: `encoding name`, `default encoding` |
| `memory`, `cartridge`, the memory kinds | the `memory` block (spec 8.4) |
| `_` | the loop variable of `for`, the last arm of `match`, an array length `[T; _]`, a padding field |
| `A`, `X`, `Y`, `AX`, `AY`, `XY`, `carry`, `zero`, `negative`, `overflow` | after `using` and `keeps` (case-sensitive); `a`, `x`, `y` in either case in assembly |

The type names (`u8`, `i8`, `u16`, `i16`, `bool`, and `int`, `real`, `label` in compile-time code) and the built-ins (`addr`, `sizeof`, `len`, `offset`, `include_bytes`, `repeat`, `label`, the math functions of 4.1) are names that the checker resolves, not keywords.

## 13.3 Files and declarations

```
File          = [ ModuleHeader END ] { Import END }
                { ConstDecl | ConstFnDecl }               (* constants a memory block may use (spec 8.4) *)
                [ MemoryBlock END ]
                { TopDecl }

ModuleHeader  = "module" IDENT "(" [ ModuleParam { "," ModuleParam } [ "," ] ] ")"   (* the file's first line (spec 2.2) *)
ModuleParam   = IDENT ":" Type "=" Expr
Import        = "import" IDENT [ "(" [ ImportArg { "," ImportArg } [ "," ] ] ")" ]
                [ "@" IDENT [ "[" Expr "]" ] ]           (* an overlay area, or bank n of a bank area (spec 8.5) *)
                [ "implements" IDENT { "," IDENT } ]
ImportArg     = IDENT "=" Expr

MemoryBlock   = "memory" "{" [ "cartridge" IDENT { IDENT } END ] { MemoryLine END } "}"
MemoryLine    = Expr "..=" Expr MemoryKind
MemoryKind    = "program" | "zeropage" | "reserved" | "volatile"
              | ( "region" | "overlay" | "bank" | "chr" ) IDENT

TopDecl       = [ "private" ] NamedDecl
              | "assert" Expr "," STRING END             (* 4.1 *)
              | "export" IDENT { "," IDENT } [ "@" Expr ] END   (* 12.3 *)
              | "default" "encoding" EncodingName END    (* 2.1 *)
NamedDecl     = ConstDecl | ConstFnDecl | GlobalVar | DataDecl | StructDecl | FnDecl | EncodingDecl

ConstDecl     = "const" IDENT [ ":" Type ] "=" Expr END
GlobalVar     = { "noinit" | "volatile" } [ "linear" | "indexed" ] "var" VarRest END
LocalVar      = [ "linear" | "indexed" ] "var" VarRest END
VarRest       = IDENT [ ":" Type ] [ Placement ] [ "=" Expr ]
DataDecl      = [ "linear" ] "data" IDENT [ ":" Type ] [ Placement ] "=" Expr END
Placement     = "@" ( "zp"                               (* not on data *)
                    | "align" "(" Expr ")"
                    | Expr )                             (* an address, a region or chr area, or area + offset (spec 4) *)

StructDecl    = "struct" IDENT "{" [ Field { Sep Field } [ Sep ] ] "}" END
Field         = IDENT ":" Type [ "@" Expr ]              (* the name "_" is a padding field (spec 3.3) *)
Sep           = "," | NL

EncodingDecl  = "encoding" IDENT [ ":" EncodingName ] "{" [ EncodingEntry { Sep EncodingEntry } [ Sep ] ] "}" END
EncodingEntry = CHAR [ "..=" CHAR ] "=" Expr             (* CHAR without a prefix *)
EncodingName  = IDENT [ "." IDENT ]

FnDecl        = "fn" Signature ( [ Keeps ] Block | "@" Expr [ Keeps ] END )   (* "@": a routine at a fixed address (spec 6.6) *)
              | ( "inline" | "interrupt" ) "fn" Signature [ Keeps ] Block
              | [ "inline" ] "asm" "fn" Signature [ Keeps ] AsmFnBody
ConstFnDecl   = "const" "fn" Signature Block             (* 4.1 *)
Signature     = IDENT "(" [ Param { "," Param } ] ")" [ "->" Type [ "using" Register ] ]
Param         = IDENT ":" Type [ "using" Register ]
              | Expr "using" Register                    (* a fixed input: 3 using X, true using carry (spec 6.6) *)
Keeps         = "keeps" Register { "," Register }
Register      = "A" | "X" | "Y" | "AX" | "AY" | "XY" | "carry" | "zero" | "negative" | "overflow"

Type          = IDENT [ "." IDENT ]                      (* u8, Enemy, c64.Vic *)
              | "ptr" Type
              | "[" Type ";" ( Expr | "_" ) "]"
```

## 13.4 Statements

```
Block         = "{" { Statement } "}"
Statement     = LocalVar
              | ConstDecl
              | IfStmt END
              | "while" Expr Block END
              | "for" IDENT "in" Range [ "using" Register ] Block END     (* the name "_": no loop variable (spec 5.1) *)
              | [ "indexed" ] "match" Expr "{" { Arm } "}" END
              | "break" END
              | "continue" END
              | "return" [ Expr ] END
              | Block END
              | "asm" AsmBody END
              | Expr [ AssignOp Expr ] END              (* without AssignOp, the checker accepts only a call *)
              | "emit" AsmBody END                     (* only in a const fn (spec 4.1) *)
              | "assert" Expr "," STRING END           (* only in a const fn (spec 4.1) *)
IfStmt        = "if" Expr Block [ "else" ( IfStmt | Block ) ]
Range         = Expr ( ".." | "..=" ) Expr [ "step" Expr ]
Arm           = ( "_" | Pattern { "," Pattern } ) "=>" ( Block END | Statement )
Pattern       = Expr [ "..=" Expr ]
AssignOp      = "=" | "+=" | "-=" | "&=" | "|=" | "^=" | "<<=" | ">>="
```

- In the header of `if`, `while`, `for` and `match`, a name followed by `{` is the name and then the block, never a struct literal.
- Assignment is a statement, not an expression (spec 3.6). The target is any place the checker accepts: a variable, `a[i]`, `s.field`, `p[i]`, `p.field`.

## 13.5 Expressions

Loosest first; the levels are those of 3.6:

```
Expr          = AndExpr { "||" AndExpr }
AndExpr       = CompareExpr { "&&" CompareExpr }
CompareExpr   = BitOrExpr [ CompareOp BitOrExpr ]      (* compares do not chain (spec 3.6) *)
CompareOp     = "==" | "!=" | "<" | "<=" | ">" | ">="
BitOrExpr     = BitXorExpr { "|" BitXorExpr }
BitXorExpr    = BitAndExpr { "^" BitAndExpr }
BitAndExpr    = ShiftExpr { "&" ShiftExpr }
ShiftExpr     = SumExpr { ( "<<" | ">>" ) SumExpr }
SumExpr       = ProductExpr { ( "+" | "-" ) ProductExpr }
ProductExpr   = UnaryExpr { ( "*" | "/" | "%" ) UnaryExpr }   (* constant expressions only (spec 3.1) *)
UnaryExpr     = ( "-" | "+" | "~" | "!" ) UnaryExpr | PostfixExpr
PostfixExpr   = Primary { "[" Expr "]" | "." IDENT | "as" Type }

Primary       = NUMBER | REAL | CHAR | STRING | "true" | "false"
              | "(" Expr ")"
              | Name [ "(" [ Expr { "," Expr } ] ")" ]     (* a name, or a call *)
              | Name "{" [ FieldValue { Sep FieldValue } [ Sep ] ] "}"   (* a struct literal: initial values only (spec 3.3) *)
              | "[" [ Expr { "," Expr } [ "," ] ] "]"     (* a list: initial values only *)
              | "[" Expr "for" IDENT "in" Range "]"       (* a list built at compile time (spec 4.1) *)
              | "." IDENT                                 (* only in assembly: a local label *)
Name          = IDENT [ "." IDENT ]                        (* module.name: text.put *)
FieldValue    = IDENT "=" Expr
```

- `a.b` is a qualified name when `a` is a module the file imports (or when `(` follows `a.b`, so the checker can suggest the import); otherwise it is `a` with the member `b`. Only a name or a qualified name can be called: there are no calls through values (spec 6.5).
- Binary operators are left associative: `a - b - c` is `(a - b) - c`.
- Built-ins are calls: `addr(x)`, `len(t)`, `sizeof(Enemy)`, `offset(Enemy.hp)`, `include_bytes("ship.bin")`, `repeat(0, 40)`.
- A `STRING` is only an initial value (spec 2.1); the checker reports it elsewhere.

## 13.6 Assembly

```
AsmBody       = "{" { AsmLine NL } "}"                 (* the last line may end at "}" *)
AsmFnBody     = "{" { "var" IDENT ":" Type NL } { AsmLine NL } "}"   (* work variables first (spec 9) *)
AsmLine       = "." IDENT ":"                          (* a local label *)
              | IDENT ":"                              (* only in emit: places a label() value (spec 4.1) *)
              | MNEMONIC [ Operand ]
              | Name "(" [ Expr { "," Expr } ] ")"     (* a code generator call (spec 4.1) *)
MNEMONIC      = IDENT                                  (* a mnemonic of the target CPU, in any case *)
Operand       = "#" Expr                               (* immediate *)
              | "a"                                    (* the accumulator, for asl, lsr, rol, ror; "A" too *)
              | "(" Expr "," "x" ")"                   (* (zp,x) *)
              | "(" Expr ")" "," "y"                   (* (zp),y *)
              | "(" Expr ")"                           (* jmp (vector) *)
              | Expr [ "," ( "x" | "y" ) ]             (* zp, abs, zp,x, abs,x, abs,y; x and y in either case *)
```

- One instruction per line; a `;` is an error in assembly (spec 9). An operand's `Expr` may name a local label (`.loop`, 13.5) and uses `.lo` / `.hi` for the bytes of an address.
- An `asm { }` statement has no `var` lines, and an `asm fn` has none after its first instruction.

## 13.7 Planned forms

Not built; each gives "not supported yet" or another error today:

- `linear` before an array field of structs: `linear parts: [Point; 4]` (spec 3.3).
- Calls through pointers: `var handler: fn() targets [idle, walk, attack] = idle`, then `handler()` (spec 6.5).
- Quoted import paths and aliases: `import "levels/level1.tern"`, `import my_io = "./io.tern"` (spec 2.2).
