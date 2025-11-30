using System;
using System.IO;
using System.Text;

namespace SRTHost
{
    public class LogTextWriter : StreamWriter
    {
        public override Encoding Encoding => base.Encoding;
        private readonly TextWriter originalTextWriter;

        public LogTextWriter(Stream stream, Encoding encoding, TextWriter originalTextWriter) : base(stream, encoding)
        {
            this.originalTextWriter = originalTextWriter;
            base.AutoFlush = true;
        }

        public override void Write(char value)
        {
            originalTextWriter?.Write(value);
            base.Write(value);
        }

        public override void Write(char[]? buffer)
        {
            originalTextWriter?.Write(buffer);
            base.Write(buffer);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            originalTextWriter?.Write(buffer, index, count);
            base.Write(buffer, index, count);
        }

        public override void Write(string? value)
        {
            originalTextWriter?.Write(value);
            base.Write(value);
        }

        public override void Write(object? value)
        {
            originalTextWriter?.Write(value);
            base.Write(value);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            originalTextWriter?.Write(buffer);
            base.Write(buffer);
        }

        public override void Write(StringBuilder? value)
        {
            originalTextWriter?.Write(value);
            base.Write(value);
        }

        // Overriding these causes duplicates?
        //public override void Write(string format, params object[] arg)
        //{
        //    originalTextWriter?.Write(format, arg);
        //    base.Write(format, arg);
        //}

        //public override void Write(string format, object arg0)
        //{
        //    originalTextWriter?.Write(format, arg0);
        //    base.Write(format, arg0);
        //}

        //public override void Write(string format, object arg0, object arg1)
        //{
        //    originalTextWriter?.Write(format, arg0, arg1);
        //    base.Write(format, arg0, arg1);
        //}

        //public override void Write(string format, object arg0, object arg1, object arg2)
        //{
        //    originalTextWriter?.Write(format, arg0, arg1, arg2);
        //    base.Write(format, arg0, arg1, arg2);
        //}

        public override void WriteLine(char value)
        {
            originalTextWriter?.WriteLine(value);
            base.WriteLine(value);
        }

        public override void WriteLine(char[]? buffer)
        {
            originalTextWriter?.WriteLine(buffer);
            base.WriteLine(buffer);
        }

        public override void WriteLine(char[] buffer, int index, int count)
        {
            originalTextWriter?.WriteLine(buffer, index, count);
            base.WriteLine(buffer, index, count);
        }

        public override void WriteLine(string? value)
        {
            originalTextWriter?.WriteLine(value);
            base.WriteLine(value);
        }

        public override void WriteLine(object? value)
        {
            originalTextWriter?.WriteLine(value);
            base.WriteLine(value);
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            originalTextWriter?.WriteLine(buffer);
            base.WriteLine(buffer);
        }

        public override void WriteLine(StringBuilder? value)
        {
            originalTextWriter?.WriteLine(value);
            base.WriteLine(value);
        }

        // Overriding these causes duplicates?
        //public override void WriteLine(string format, params object[] arg)
        //{
        //    originalTextWriter?.WriteLine(format, arg);
        //    base.WriteLine(format, arg);
        //}

        //public override void WriteLine(string format, object arg0)
        //{
        //    originalTextWriter?.WriteLine(format, arg0);
        //    base.WriteLine(format, arg0);
        //}

        //public override void WriteLine(string format, object arg0, object arg1)
        //{
        //    originalTextWriter?.WriteLine(format, arg0, arg1);
        //    base.WriteLine(format, arg0, arg1);
        //}

        //public override void WriteLine(string format, object arg0, object arg1, object arg2)
        //{
        //    originalTextWriter?.WriteLine(format, arg0, arg1, arg2);
        //    base.WriteLine(format, arg0, arg1, arg2);
        //}

        public override void Flush()
        {
            originalTextWriter?.Flush();
            base.Flush();
        }

        public override void Close()
        {
            originalTextWriter?.Close();
            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                originalTextWriter?.Dispose();
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
