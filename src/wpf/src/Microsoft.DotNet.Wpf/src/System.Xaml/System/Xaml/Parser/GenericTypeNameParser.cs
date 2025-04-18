﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Runtime.Serialization;
using System.Xaml.Schema;

namespace MS.Internal.Xaml.Parser
{
    internal class GenericTypeNameParser
    {
        [Serializable]
        private class TypeNameParserException : Exception
        {
            public TypeNameParserException(string message)
                : base(message)
            {
            }
#pragma warning disable SYSLIB0051 // Type or member is obsolete
            protected TypeNameParserException(SerializationInfo si, StreamingContext sc) : base(si, sc)
            {
            }
#pragma warning restore SYSLIB0051 // Type or member is obsolete
        }

        private GenericTypeNameScanner _scanner;
        private string _inputText;
        private Func<string, string> _prefixResolver;
        private Stack<TypeNameFrame> _stack;

        public GenericTypeNameParser(Func<string, string> prefixResolver)
        {
            _prefixResolver = prefixResolver;
        }

        public static XamlTypeName ParseIfTrivalName(string text, Func<string, string> prefixResolver, out string error)
        {
            if (text.Contains('(') || text.Contains('['))
            {
                error = string.Empty;
                return null;
            }

            string prefix;
            string simpleName;

            error = string.Empty;
            if (!XamlQualifiedName.Parse(text, out prefix, out simpleName))
            {
                error = SR.Format(SR.InvalidTypeString, text);
                return null;
            }

            string ns = prefixResolver(prefix);
            if (string.IsNullOrEmpty(ns))
            {
                error = SR.Format(SR.PrefixNotFound, prefix);
                return null;
            }

            XamlTypeName xamlTypeName = new XamlTypeName(ns, simpleName);
            return xamlTypeName;
        }

        public XamlTypeName ParseName(string text, out string error)
        {
            error = string.Empty;
            _scanner = new GenericTypeNameScanner(text);
            _inputText = text;

            StartStack();

            try
            {
                _scanner.Read();
                P_XamlTypeName();
                if (_scanner.Token != GenericTypeNameScannerToken.NONE)
                {
                    ThrowOnBadInput();
                }
            }
            catch (TypeNameParserException ex)
            {
                error = ex.Message;
            }

            XamlTypeName typeName = null;
            if (string.IsNullOrEmpty(error))
            {
                typeName = CollectNameFromStack();
            }

            return typeName;
        }

        public IList<XamlTypeName> ParseList(string text, out string error)
        {
            _scanner = new GenericTypeNameScanner(text);
            _inputText = text;
            StartStack();

            error = string.Empty;
            try
            {
                _scanner.Read();
                P_XamlTypeNameList();
                if (_scanner.Token != GenericTypeNameScannerToken.NONE)
                {
                    ThrowOnBadInput();
                }
            }
            catch (TypeNameParserException ex)
            {
                error = ex.Message;
            }

            IList<XamlTypeName> typeNameList = null;
            if (string.IsNullOrEmpty(error))
            {
                typeNameList = CollectNameListFromStack();
            }

            return typeNameList;
        }

        // XamlTypeName     ::= SimpleTypeName TypeParameters? Subscript*
        // SimpleTypeName   ::= (Prefix ‘:’)? TypeName
        // TypeParameters   ::= ‘(‘ XamlTypeNameList ‘)’
        // XamlTypeNameList ::= XamlTypeName NameListExt*
        // NameListExt      ::= ‘,’ XamlTypeName
        // Subscript        ::= ‘[’ ‘,’* ‘]’

        // XamlTypeName     ::= SimpleTypeName TypeParameters? Subscript*
        //
        private void P_XamlTypeName()
        {
            // Required
            if (_scanner.Token != GenericTypeNameScannerToken.NAME)
            {
                ThrowOnBadInput();
            }

            P_SimpleTypeName();

            // Optional
            if (_scanner.Token == GenericTypeNameScannerToken.OPEN)
            {
                P_TypeParameters();
            }

            // Optional
            if (_scanner.Token == GenericTypeNameScannerToken.SUBSCRIPT)
            {
                P_RepeatingSubscript();
            }

            Callout_EndOfType();
        }

        // SimpleTypeName   ::= (Prefix ‘:’)? TypeName
        //
        private void P_SimpleTypeName()
        {
            // caller checks this.
            Debug.Assert(_scanner.Token == GenericTypeNameScannerToken.NAME);

            string prefix = string.Empty;
            string name = _scanner.MultiCharTokenText;
            _scanner.Read();

            // Colon is optional.
            if (_scanner.Token == GenericTypeNameScannerToken.COLON)
            {
                prefix = name;
                _scanner.Read();

                // IF there was a colon then there must be a name following.
                if (_scanner.Token != GenericTypeNameScannerToken.NAME)
                {
                    ThrowOnBadInput();
                }

                name = _scanner.MultiCharTokenText;
                _scanner.Read();
            }

            Callout_FoundName(prefix, name);
        }

        // TypeParameters   ::= ‘(‘ XamlTypeNameList ‘)’
        //
        private void P_TypeParameters()
        {
            // Required
            // caller checks this.
            Debug.Assert(_scanner.Token == GenericTypeNameScannerToken.OPEN);
            _scanner.Read();

            P_XamlTypeNameList();

            // Required
            if (_scanner.Token != GenericTypeNameScannerToken.CLOSE)
            {
                ThrowOnBadInput();
            }

            _scanner.Read();
        }

        // XamlTypeNameList ::= XamlTypeName NameListExt*
        //
        private void P_XamlTypeNameList()
        {
            P_XamlTypeName();

            // optional zero or more.
            while (_scanner.Token == GenericTypeNameScannerToken.COMMA)
            {
                P_NameListExt();
            }
        }

        // NameListExt      ::= ‘,’ XamlTypeName
        //
        private void P_NameListExt()
        {
            // Caller checked this.
            Debug.Assert(_scanner.Token == GenericTypeNameScannerToken.COMMA);
            _scanner.Read();

            P_XamlTypeName();
        }

        // Subscript        ::= ‘[’ ‘,’* ‘]’
        //
        private void P_RepeatingSubscript()
        {
            // caller checks this.
            Debug.Assert(_scanner.Token == GenericTypeNameScannerToken.SUBSCRIPT);

            do
            {
                Callout_Subscript(_scanner.MultiCharTokenText);
                _scanner.Read();
            }
            while (_scanner.Token == GenericTypeNameScannerToken.SUBSCRIPT);
        }

        private void ThrowOnBadInput()
        {
            throw new TypeNameParserException(SR.Format(SR.InvalidCharInTypeName, _scanner.ErrorCurrentChar, _inputText));
        }

        private void StartStack()
        {
            _stack = new Stack<TypeNameFrame>();
            TypeNameFrame frame;
            frame = new TypeNameFrame();
            _stack.Push(frame);
        }

        private void Callout_FoundName(string prefix, string name)
        {
            TypeNameFrame frame = new TypeNameFrame
            {
                Name = name
            };
            string ns = _prefixResolver(prefix);
            frame.Namespace = ns ?? throw new TypeNameParserException(SR.Format(SR.PrefixNotFound, prefix));
            _stack.Push(frame);
        }

        private void Callout_EndOfType()
        {
            TypeNameFrame frame = _stack.Pop();
            XamlTypeName typeName = new XamlTypeName(frame.Namespace, frame.Name, frame.TypeArgs);

            frame = _stack.Peek();
            if (frame.TypeArgs is null)
            {
                frame.AllocateTypeArgs();
            }

            frame.TypeArgs.Add(typeName);
        }

        private void Callout_Subscript(string subscript)
        {
            TypeNameFrame frame = _stack.Peek();
            frame.Name += subscript;
        }

        private XamlTypeName CollectNameFromStack()
        {
            if (_stack.Count != 1)
            {
                throw new TypeNameParserException(SR.Format(SR.InvalidTypeString, _inputText));
            }

            TypeNameFrame frame = _stack.Peek();
            if (frame.TypeArgs.Count != 1)
            {
                throw new TypeNameParserException(SR.Format(SR.InvalidTypeString, _inputText));
            }

            XamlTypeName xamlTypeName = frame.TypeArgs[0];
            return xamlTypeName;
        }

        private IList<XamlTypeName> CollectNameListFromStack()
        {
            if (_stack.Count != 1)
            {
                throw new TypeNameParserException(SR.Format(SR.InvalidTypeString, _inputText));
            }

            TypeNameFrame frame = _stack.Peek();

            List<XamlTypeName> xamlTypeNameList = frame.TypeArgs;
            return xamlTypeNameList;
        }
    }

    internal class TypeNameFrame
    {
        private List<XamlTypeName> _typeArgs;

        public string Namespace { get; set; }
        public string Name { get; set; }
        public List<XamlTypeName> TypeArgs { get { return _typeArgs; } }

        public void AllocateTypeArgs()
        {
            _typeArgs = new List<XamlTypeName>();
        }
    }
}
