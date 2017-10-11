// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    // This class implements a text writer that writes to a string buffer and allows
    // the resulting sequence of characters to be presented as a string.
    public class StringWriter : TextWriter
    {
        private static volatile UnicodeEncoding s_encoding = null;

        private StringBuilder _sb;
        private bool _isOpen;

        // Constructs a new StringWriter. A new StringBuilder is automatically
        // created and associated with the new StringWriter.
        public StringWriter()
            : this(new StringBuilder(), CultureInfo.CurrentCulture)
        {
        }

        public StringWriter(IFormatProvider formatProvider)
            : this(new StringBuilder(), formatProvider)
        {
        }

        // Constructs a new StringWriter that writes to the given StringBuilder.
        // 
        public StringWriter(StringBuilder sb) : this(sb, CultureInfo.CurrentCulture)
        {
        }

        public StringWriter(StringBuilder sb, IFormatProvider formatProvider) : base(formatProvider)
        {
            if (sb == null)
            {
                throw new ArgumentNullException(nameof(sb), SR.ArgumentNull_Buffer);
            }

            _sb = sb;
            _isOpen = true;
        }

        public override void Close()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            // Do not destroy _sb, so that we can extract this after we are
            // done writing (similar to MemoryStream's GetBuffer & ToArray methods)
            _isOpen = false;
            base.Dispose(disposing);
        }


        public override Encoding Encoding
        {
            get
            {
                if (s_encoding == null)
                {
                    s_encoding = new UnicodeEncoding(false, false);
                }
                return s_encoding;
            }
        }

        // Returns the underlying StringBuilder. This is either the StringBuilder
        // that was passed to the constructor, or the StringBuilder that was
        // automatically created.
        //
        public virtual StringBuilder GetStringBuilder()
        {
            return _sb;
        }

        // Writes a character to the underlying string buffer.
        //
        public override void Write(char value)
        {
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_WriterClosed);
            }

            _sb.Append(value);
        }

        // Writes a range of a character array to the underlying string buffer.
        // This method will write count characters of data into this
        // StringWriter from the buffer character array starting at position
        // index.
        //
        public override void Write(char[] buffer, int index, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer), SR.ArgumentNull_Buffer);
            }
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), SR.ArgumentOutOfRange_NeedNonNegNum);
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), SR.ArgumentOutOfRange_NeedNonNegNum);
            }
            if (buffer.Length - index < count)
            {
                throw new ArgumentException(SR.Argument_InvalidOffLen);
            }
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_WriterClosed);
            }

            _sb.Append(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> source)
        {
            if (GetType() != typeof(StringWriter))
            {
                // This overload was added affter the Write(char[], ...) overload, and so in case
                // a derived type may have overridden it, we need to delegate to it, which the base does.
                base.Write(source);
                return;
            }

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_WriterClosed);
            }

            _sb.Append(source);
        }

        // Writes a string to the underlying string buffer. If the given string is
        // null, nothing is written.
        //
        public override void Write(string value)
        {
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_WriterClosed);
            }

            if (value != null)
            {
                _sb.Append(value);
            }
        }

        public override void WriteLine(ReadOnlySpan<char> source)
        {
            if (GetType() != typeof(StringWriter))
            {
                // This overload was added affter the WriteLine(char[], ...) overload, and so in case
                // a derived type may have overridden it, we need to delegate to it, which the base does.
                base.WriteLine(source);
                return;
            }

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_WriterClosed);
            }

            _sb.Append(source);
            WriteLine();
        }

        #region Task based Async APIs

        public override Task WriteAsync(char value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteAsync(string value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteAsync(char[] buffer, int index, int count)
        {
            Write(buffer, index, count);
            return Task.CompletedTask;
        }

        public override Task WriteAsync(ReadOnlyMemory<char> source, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            Write(source.Span);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(char value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(char[] buffer, int index, int count)
        {
            WriteLine(buffer, index, count);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> source, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            WriteLine(source.Span);
            return Task.CompletedTask;
        }

        public override Task FlushAsync()
        {
            return Task.CompletedTask;
        }
        
        #endregion

        // Returns a string containing the characters written to this TextWriter
        // so far.
        //
        public override string ToString()
        {
            return _sb.ToString();
        }
    }
}
