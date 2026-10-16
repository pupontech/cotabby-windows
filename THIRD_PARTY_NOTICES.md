# Third-party notices

## TabType — secret sanitization adaptation

`src/Cotabby.Core/Support/SecretSanitizer.cs` adapts recognized-secret rules and
Luhn validation from `Sources/TabTypeKit/Context/SecretSanitizer.swift` in
https://github.com/nilava/TabType at revision `b75ea0e`.

The C# adaptation omits broad entropy and unchecked IBAN redaction, handles
quoted assignment values, and adds bounded input and regex timeouts. This
notice does not change Cotabby's existing license.

MIT License

Copyright (c) 2026 TabType contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
