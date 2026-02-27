using System;
using System.Collections.Generic;
using System.Text;

namespace UnityAgent.Models
{
    /// <summary>
    /// Minimal VT100 screen buffer that processes raw ConPty output.
    /// Maintains a character grid + scrollback and renders to plain text.
    /// </summary>
    public sealed class VtScreenBuffer
    {
        private readonly int _cols;
        private readonly int _rows;
        private char[][] _screen;
        private int _cursorRow;
        private int _cursorCol;
        private readonly List<string> _scrollback = new();
        private const int MaxScrollbackLines = 5000;

        // Scroll region (DECSTBM), 0-based inclusive
        private int _scrollTop;
        private int _scrollBottom;

        // Parser state
        private enum State { Normal, Esc, Csi, Osc, EscIntermediate }
        private State _state = State.Normal;
        private readonly StringBuilder _csiParams = new();
        private char _csiIntermediate;
        private bool _oscEscSeen; // for detecting ESC \ as OSC terminator

        private readonly object _lock = new();

        public VtScreenBuffer(int cols, int rows)
        {
            _cols = cols;
            _rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _screen = new char[rows][];
            for (int r = 0; r < rows; r++)
            {
                _screen[r] = new char[cols];
                Array.Fill(_screen[r], ' ');
            }
        }

        public void Process(string data)
        {
            lock (_lock)
            {
                foreach (char c in data)
                    ProcessChar(c);
            }
        }

        public string Render()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();

                // Scrollback
                foreach (var line in _scrollback)
                {
                    sb.Append(line);
                    sb.Append('\n');
                }

                // Current screen
                for (int r = 0; r < _rows; r++)
                {
                    // Trim trailing spaces for cleaner display
                    int lastNonSpace = _cols - 1;
                    while (lastNonSpace >= 0 && _screen[r][lastNonSpace] == ' ')
                        lastNonSpace--;

                    for (int c = 0; c <= lastNonSpace; c++)
                        sb.Append(_screen[r][c]);

                    if (r < _rows - 1)
                        sb.Append('\n');
                }

                return sb.ToString();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _scrollback.Clear();
                for (int r = 0; r < _rows; r++)
                    Array.Fill(_screen[r], ' ');
                _cursorRow = 0;
                _cursorCol = 0;
                _scrollTop = 0;
                _scrollBottom = _rows - 1;
                _state = State.Normal;
            }
        }

        // ── Char-by-char state machine ──────────────────────────

        private void ProcessChar(char c)
        {
            switch (_state)
            {
                case State.Normal:
                    ProcessNormal(c);
                    break;
                case State.Esc:
                    ProcessEsc(c);
                    break;
                case State.Csi:
                    ProcessCsi(c);
                    break;
                case State.Osc:
                    ProcessOsc(c);
                    break;
                case State.EscIntermediate:
                    // Consume one more byte after ESC + intermediate (e.g., ESC ( B)
                    _state = State.Normal;
                    break;
            }
        }

        private void ProcessNormal(char c)
        {
            switch (c)
            {
                case '\x1b': // ESC
                    _state = State.Esc;
                    break;
                case '\n': // LF
                    LineFeed();
                    break;
                case '\r': // CR
                    _cursorCol = 0;
                    break;
                case '\x08': // BS
                    if (_cursorCol > 0) _cursorCol--;
                    break;
                case '\x07': // BEL - ignore
                    break;
                case '\t': // TAB
                    _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, _cols - 1);
                    break;
                default:
                    if (c >= ' ') // printable
                        PutChar(c);
                    // ignore other control chars
                    break;
            }
        }

        private void ProcessEsc(char c)
        {
            switch (c)
            {
                case '[': // CSI
                    _state = State.Csi;
                    _csiParams.Clear();
                    _csiIntermediate = '\0';
                    break;
                case ']': // OSC
                    _state = State.Osc;
                    _oscEscSeen = false;
                    break;
                case '(' or ')' or '*' or '+': // Character set designation
                    _state = State.EscIntermediate;
                    break;
                case 'M': // Reverse index
                    ReverseIndex();
                    _state = State.Normal;
                    break;
                case 'D': // Index (line feed)
                    LineFeed();
                    _state = State.Normal;
                    break;
                case 'E': // Next line
                    _cursorCol = 0;
                    LineFeed();
                    _state = State.Normal;
                    break;
                case '7': // Save cursor (DECSC)
                    _state = State.Normal;
                    break;
                case '8': // Restore cursor (DECRC)
                    _state = State.Normal;
                    break;
                case '=': // Application keypad (DECKPAM)
                case '>': // Normal keypad (DECKPNM)
                    _state = State.Normal;
                    break;
                default:
                    _state = State.Normal;
                    break;
            }
        }

        private void ProcessCsi(char c)
        {
            if (c >= '0' && c <= '9' || c == ';' || c == '?')
            {
                _csiParams.Append(c);
            }
            else if (c >= ' ' && c <= '/')
            {
                // Intermediate byte
                _csiIntermediate = c;
            }
            else if (c >= '@' && c <= '~')
            {
                // Final byte — execute CSI sequence
                ExecuteCsi(c);
                _state = State.Normal;
            }
            else
            {
                // Invalid — abort
                _state = State.Normal;
            }
        }

        private void ProcessOsc(char c)
        {
            if (_oscEscSeen)
            {
                // Expecting '\' for ST (String Terminator)
                _state = State.Normal;
                return;
            }

            if (c == '\x07') // BEL terminates OSC
            {
                _state = State.Normal;
            }
            else if (c == '\x1b')
            {
                _oscEscSeen = true;
            }
            // else: accumulate OSC data (we ignore it)
        }

        // ── CSI sequence dispatch ───────────────────────────────

        private void ExecuteCsi(char final)
        {
            var paramStr = _csiParams.ToString();

            // Handle private mode sequences (CSI ? ...)
            if (paramStr.Length > 0 && paramStr[0] == '?')
            {
                // DECSET/DECRST — ignore for now
                return;
            }

            switch (final)
            {
                case 'H': // CUP — Cursor Position
                case 'f': // HVP — Horizontal Vertical Position
                    CursorPosition(paramStr);
                    break;
                case 'A': // CUU — Cursor Up
                    CursorUp(ParseFirst(paramStr, 1));
                    break;
                case 'B': // CUD — Cursor Down
                    CursorDown(ParseFirst(paramStr, 1));
                    break;
                case 'C': // CUF — Cursor Forward
                    CursorForward(ParseFirst(paramStr, 1));
                    break;
                case 'D': // CUB — Cursor Back
                    CursorBack(ParseFirst(paramStr, 1));
                    break;
                case 'E': // CNL — Cursor Next Line
                    _cursorCol = 0;
                    CursorDown(ParseFirst(paramStr, 1));
                    break;
                case 'F': // CPL — Cursor Previous Line
                    _cursorCol = 0;
                    CursorUp(ParseFirst(paramStr, 1));
                    break;
                case 'G': // CHA — Cursor Horizontal Absolute
                    _cursorCol = Math.Clamp(ParseFirst(paramStr, 1) - 1, 0, _cols - 1);
                    break;
                case 'd': // VPA — Vertical Position Absolute
                    _cursorRow = Math.Clamp(ParseFirst(paramStr, 1) - 1, 0, _rows - 1);
                    break;
                case 'J': // ED — Erase in Display
                    EraseInDisplay(ParseFirst(paramStr, 0));
                    break;
                case 'K': // EL — Erase in Line
                    EraseInLine(ParseFirst(paramStr, 0));
                    break;
                case 'm': // SGR — Select Graphic Rendition (colors/styles — ignore)
                    break;
                case 'L': // IL — Insert Lines
                    InsertLines(ParseFirst(paramStr, 1));
                    break;
                case 'M': // DL — Delete Lines
                    DeleteLines(ParseFirst(paramStr, 1));
                    break;
                case 'S': // SU — Scroll Up
                    ScrollUp(ParseFirst(paramStr, 1));
                    break;
                case 'T': // SD — Scroll Down
                    ScrollDown(ParseFirst(paramStr, 1));
                    break;
                case 'r': // DECSTBM — Set Scrolling Region
                    SetScrollRegion(paramStr);
                    break;
                case 'P': // DCH — Delete Characters
                    DeleteChars(ParseFirst(paramStr, 1));
                    break;
                case '@': // ICH — Insert Characters
                    InsertChars(ParseFirst(paramStr, 1));
                    break;
                case 'X': // ECH — Erase Characters
                    EraseChars(ParseFirst(paramStr, 1));
                    break;
                case 'h': // SM — Set Mode (ignore)
                case 'l': // RM — Reset Mode (ignore)
                case 'n': // DSR — Device Status Report (ignore)
                case 'c': // DA — Device Attributes (ignore)
                case 'q': // DECLL / DECSCUSR (ignore)
                    break;
                // Space + final byte sequences (e.g., CSI n SP q)
                default:
                    break;
            }
        }

        // ── Cursor movement ─────────────────────────────────────

        private void CursorPosition(string paramStr)
        {
            var parts = paramStr.Split(';');
            int row = parts.Length >= 1 && int.TryParse(parts[0], out var r) ? r : 1;
            int col = parts.Length >= 2 && int.TryParse(parts[1], out var c2) ? c2 : 1;
            _cursorRow = Math.Clamp(row - 1, 0, _rows - 1);
            _cursorCol = Math.Clamp(col - 1, 0, _cols - 1);
        }

        private void CursorUp(int n) =>
            _cursorRow = Math.Max(_cursorRow - n, 0);

        private void CursorDown(int n) =>
            _cursorRow = Math.Min(_cursorRow + n, _rows - 1);

        private void CursorForward(int n) =>
            _cursorCol = Math.Min(_cursorCol + n, _cols - 1);

        private void CursorBack(int n) =>
            _cursorCol = Math.Max(_cursorCol - n, 0);

        // ── Scrolling ───────────────────────────────────────────

        private void LineFeed()
        {
            if (_cursorRow == _scrollBottom)
            {
                ScrollUp(1);
            }
            else if (_cursorRow < _rows - 1)
            {
                _cursorRow++;
            }
        }

        private void ReverseIndex()
        {
            if (_cursorRow == _scrollTop)
            {
                ScrollDown(1);
            }
            else if (_cursorRow > 0)
            {
                _cursorRow--;
            }
        }

        private void ScrollUp(int n)
        {
            for (int i = 0; i < n; i++)
            {
                // If scrolling from the top of the screen, add to scrollback
                if (_scrollTop == 0)
                {
                    var line = new string(_screen[0]).TrimEnd();
                    _scrollback.Add(line);
                    if (_scrollback.Count > MaxScrollbackLines)
                        _scrollback.RemoveAt(0);
                }

                // Shift lines up within scroll region
                for (int r = _scrollTop; r < _scrollBottom; r++)
                    _screen[r] = _screen[r + 1];

                _screen[_scrollBottom] = new char[_cols];
                Array.Fill(_screen[_scrollBottom], ' ');
            }
        }

        private void ScrollDown(int n)
        {
            for (int i = 0; i < n; i++)
            {
                // Shift lines down within scroll region
                for (int r = _scrollBottom; r > _scrollTop; r--)
                    _screen[r] = _screen[r - 1];

                _screen[_scrollTop] = new char[_cols];
                Array.Fill(_screen[_scrollTop], ' ');
            }
        }

        private void SetScrollRegion(string paramStr)
        {
            if (string.IsNullOrEmpty(paramStr))
            {
                _scrollTop = 0;
                _scrollBottom = _rows - 1;
            }
            else
            {
                var parts = paramStr.Split(';');
                int top = parts.Length >= 1 && int.TryParse(parts[0], out var t) ? t : 1;
                int bottom = parts.Length >= 2 && int.TryParse(parts[1], out var b) ? b : _rows;
                _scrollTop = Math.Clamp(top - 1, 0, _rows - 1);
                _scrollBottom = Math.Clamp(bottom - 1, 0, _rows - 1);
                if (_scrollTop >= _scrollBottom)
                {
                    _scrollTop = 0;
                    _scrollBottom = _rows - 1;
                }
            }
            // DECSTBM also homes the cursor
            _cursorRow = 0;
            _cursorCol = 0;
        }

        // ── Erasing ─────────────────────────────────────────────

        private void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0: // Erase below (from cursor to end of screen)
                    EraseInLine(0);
                    for (int r = _cursorRow + 1; r < _rows; r++)
                        Array.Fill(_screen[r], ' ');
                    break;
                case 1: // Erase above (from start to cursor)
                    EraseInLine(1);
                    for (int r = 0; r < _cursorRow; r++)
                        Array.Fill(_screen[r], ' ');
                    break;
                case 2: // Erase all
                case 3: // Erase all + scrollback
                    for (int r = 0; r < _rows; r++)
                        Array.Fill(_screen[r], ' ');
                    if (mode == 3)
                        _scrollback.Clear();
                    break;
            }
        }

        private void EraseInLine(int mode)
        {
            switch (mode)
            {
                case 0: // Erase from cursor to end of line
                    for (int c = _cursorCol; c < _cols; c++)
                        _screen[_cursorRow][c] = ' ';
                    break;
                case 1: // Erase from start to cursor
                    for (int c = 0; c <= _cursorCol; c++)
                        _screen[_cursorRow][c] = ' ';
                    break;
                case 2: // Erase entire line
                    Array.Fill(_screen[_cursorRow], ' ');
                    break;
            }
        }

        // ── Character insertion/deletion ────────────────────────

        private void InsertLines(int n)
        {
            n = Math.Min(n, _scrollBottom - _cursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                for (int r = _scrollBottom; r > _cursorRow; r--)
                    _screen[r] = _screen[r - 1];
                _screen[_cursorRow] = new char[_cols];
                Array.Fill(_screen[_cursorRow], ' ');
            }
        }

        private void DeleteLines(int n)
        {
            n = Math.Min(n, _scrollBottom - _cursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                for (int r = _cursorRow; r < _scrollBottom; r++)
                    _screen[r] = _screen[r + 1];
                _screen[_scrollBottom] = new char[_cols];
                Array.Fill(_screen[_scrollBottom], ' ');
            }
        }

        private void DeleteChars(int n)
        {
            n = Math.Min(n, _cols - _cursorCol);
            for (int c = _cursorCol; c < _cols - n; c++)
                _screen[_cursorRow][c] = _screen[_cursorRow][c + n];
            for (int c = _cols - n; c < _cols; c++)
                _screen[_cursorRow][c] = ' ';
        }

        private void InsertChars(int n)
        {
            n = Math.Min(n, _cols - _cursorCol);
            for (int c = _cols - 1; c >= _cursorCol + n; c--)
                _screen[_cursorRow][c] = _screen[_cursorRow][c - n];
            for (int c = _cursorCol; c < _cursorCol + n; c++)
                _screen[_cursorRow][c] = ' ';
        }

        private void EraseChars(int n)
        {
            n = Math.Min(n, _cols - _cursorCol);
            for (int c = _cursorCol; c < _cursorCol + n; c++)
                _screen[_cursorRow][c] = ' ';
        }

        // ── Put character ───────────────────────────────────────

        private void PutChar(char c)
        {
            if (_cursorCol >= _cols)
            {
                // Wrap to next line
                _cursorCol = 0;
                LineFeed();
            }

            _screen[_cursorRow][_cursorCol] = c;
            _cursorCol++;
        }

        // ── Param parsing helpers ───────────────────────────────

        private static int ParseFirst(string paramStr, int defaultVal)
        {
            if (string.IsNullOrEmpty(paramStr))
                return defaultVal;
            var parts = paramStr.Split(';');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var v) && v > 0)
                return v;
            return defaultVal;
        }
    }
}
