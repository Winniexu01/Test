// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


//------------------------------------------------------------------------------
//

//
// Description: CodeSink which writes to a text file. Translates \n to \r\n.
//              Trims blank lines.
//
//              Also performs "sd add" and "sd edit" commands as appropriate.
//
//              Limitations:
//
//              * No "sd delete" functionality. Right now you need to detect
//                stale files and "sd delete" them manually.
//
//              * It's slow to call sd.exe for each edited file. Workaround:
//                manually "sd edit Blah/..." before running the script. (Then
//                this automation is more to prevent you from forgetting files.)
//

namespace MS.Internal.MilCodeGen.Runtime
{
    using System;
    using System.IO;
    using System.Text;
    using System.Diagnostics;

    public class FileCodeSink : CodeSink, IDisposable
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors
        public FileCodeSink(string dir, string filename): this(dir, filename, true) { }

        public FileCodeSink(string dir, string filename, bool createDirIfNecessary)
        {
            _filePath = Path.Combine(dir, filename);

            if (createDirIfNecessary)
            {
                Directory.CreateDirectory(dir);
            }

            _streamWriter = new StreamWriter(_filePath, false, Encoding.ASCII);
        }

        ~FileCodeSink()
        {
            Dispose(false);
        }
        #endregion Constructors

        //------------------------------------------------------
        //
        //  Public Methods
        //
        //------------------------------------------------------
        
        #region Public Methods
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        #endregion Public Methods

        //------------------------------------------------------
        //
        //  Public Properties
        //
        //------------------------------------------------------
        
        #region Public Properties

        public string FilePath
        {
            get { return _filePath; }
        }

        public string FileName
        {
            get { return Path.GetFileName(_filePath); }
        }

        #endregion Public Properties

        //------------------------------------------------------
        //
        //  Protected Methods
        //
        //------------------------------------------------------

        #region Protected Methods
        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (_streamWriter != null)
                    {
                        FlushCurrentLine();
                        _streamWriter.Close();
                        _streamWriter = null;
                    }

                    LogCreation();
                }

                disposed = true;         
            }
        }
        #endregion Protected Methods
        
        
        //------------------------------------------------------
        //
        //  Protected Internal Methods
        //
        //------------------------------------------------------
        
        #region Protected Internal Methods
        protected internal override void InternalWrite(string output)
        {
            if (output.IndexOf('\r') >= 0) {
                throw new Exception("Internal error");
            }

            string[] lines = output.Split('\n');

            for (int i=0; i<lines.Length; i++)
            {
                _currentLine += lines[i];
                if (i < lines.Length - 1)
                {
                    FlushCurrentLine();
                    _streamWriter.Write("\r\n");
                }
            }
        }
        #endregion
        
        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        #region Private Methods
        private void FlushCurrentLine()
        {
            if (_currentLine != "")
            {
                if (_currentLine.Trim() != "")
                {
                    _streamWriter.Write(_currentLine);
                }
                _currentLine = "";
            }
        }

        // Log the creation of this file, and any sd operations performed.
        private void LogCreation()
        {
            // Log the creation of this file
            Console.WriteLine("\tCreated: {0}", _filePath);
        }
        #endregion Private Methods

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        #region Private Fields
        StreamWriter _streamWriter;
        string _filePath;
        bool disposed = false;
        string _currentLine = "";
        #endregion Private Fields
    }
}




