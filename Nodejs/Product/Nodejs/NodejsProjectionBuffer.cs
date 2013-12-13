﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Ajax.Utilities;
using Microsoft.NodejsTools.Project;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.NodejsTools {
    /// <summary>
    /// Represents a projection buffer for Node.js code.  We wrap the user's code inside of a function body
    /// and include appropriate reference tags as well which are dynamically injected.
    /// 
    /// function body() {
    ///     // begin user code
    ///     // end user code
    /// };
    /// 
    /// Which is produced by doing:
    /// 
    ///      +-------------------------+
    ///      | Projection Buffer       |     elides leading and trailing
    ///      +-------------------------+
    ///                  |
    ///   +----------------------------------+
    ///   | Leading | Disk Buffer | Trailing |  Projection Buffer, adds leading and trailing
    ///   +-------------+--------------------+
    ///                 |
    ///                 |
    ///         +----------------+
    ///         |   Disk Buffer  |
    ///         +----------------+
    ///         
    /// Currently the surface buffer is a projection buffer instead of a simpler
    /// elision buffer because elision buffers don't currently work with the
    /// JavaScript language service.
    /// </summary>
    class NodejsProjectionBuffer {
        private readonly IContentTypeRegistryService _contentRegistry;
        private readonly ITextBuffer _diskBuffer;
        private readonly IContentType _contentType;
        private readonly IProjectionBuffer _projBuffer; // the buffer we project into        
        private readonly IProjectionBuffer _elisionBuffer;
        private readonly string _referenceFilename;

        private int _endOfComments;
        private string _leadingText;

        public NodejsProjectionBuffer(IContentTypeRegistryService contentRegistry, IProjectionBufferFactoryService bufferFactory, ITextBuffer diskBuffer, IBufferGraphFactoryService bufferGraphFactory, IContentType contentType, string referenceFileName) {
            _diskBuffer = diskBuffer;
            _referenceFilename = referenceFileName;
            UpdateLeadingText(_diskBuffer.CurrentSnapshot);

            _contentRegistry = contentRegistry;
            _contentType = contentType;

            _projBuffer = CreateProjectionBuffer(bufferFactory);
            _elisionBuffer = CreateElisionBuffer(bufferFactory);
            _elisionBuffer.Properties[typeof(NodejsProjectionBuffer)] = this;
            _diskBuffer.Changed += DiskBufferChanged;
        }

        private void DiskBufferChanged(object sender, TextContentChangedEventArgs e) {
            foreach (var change in e.Changes) {
                if (change.OldPosition <= _endOfComments) {
                    string oldLeading = _leadingText;

                    UpdateLeadingText(e.After);

                    if (oldLeading != _leadingText) {
                        _projBuffer.ReplaceSpans(
                            0,
                            1,
                            new[] { _leadingText },
                            EditOptions.None,
                            new ProjectionBufferBoundsEdit(_leadingText.Length)
                        );
                    }
                    break;
                }
            }
            
            NodejsPackage.Instance.ChangedBuffers.Add(e.After.TextBuffer);
        }

        private void UpdateLeadingText(ITextSnapshot current) {
            // change in the leading comments, update our list of leading comments
            var fullText = current.GetText();
            var document = new DocumentContext(null, fullText);
            var scanner = new JSScanner(new Context(document));

            var tokenContext = scanner.ScanNextToken(false);
            while (tokenContext.Token != JSToken.EndOfFile) {
                if (tokenContext.Token != JSToken.MultipleLineComment &&
                    tokenContext.Token != JSToken.SingleLineComment &&
                    tokenContext.Token != JSToken.EndOfLine) {
                    break;
                }

                tokenContext = scanner.ScanNextToken(false);
            }

            // special case divide for when the user is typing the first / at the beginning
            // of the file for adding a comment.
            _endOfComments = tokenContext.Token == JSToken.Divide ? tokenContext.EndPosition : tokenContext.StartPosition;
            string commentText = fullText.Substring(0, _endOfComments);
            _leadingText = commentText + GetBaseLeadingText();
        }

        private IProjectionBuffer CreateProjectionBuffer(IProjectionBufferFactoryService bufferFactory) {
            var res = bufferFactory.CreateProjectionBuffer(
                null,
                new object[] { 
                    LeadingText,
                    _diskBuffer.CurrentSnapshot.CreateTrackingSpan(
                        0,
                        _diskBuffer.CurrentSnapshot.Length,
                        SpanTrackingMode.EdgeInclusive,
                        TrackingFidelityMode.Forward
                    ),
                    TrailingText
                },
                ProjectionBufferOptions.None,
                _contentType
            );
            return res;
        }

        public string LeadingText {
            get {
                return _leadingText;
            }
        }

        private string GetBaseLeadingText() {
            string asyncFilePath = null;
            NodejsFileNode node = GetFileNode();
            if (node != null) {
                asyncFilePath = node._asyncFilePath;
            }

            return
                "/// <reference path=\"" + _referenceFilename + "\" />\r\n" +
                (asyncFilePath != null ? ("/// <reference path=\"" + asyncFilePath + "\" />\r\n") : "") +
                GetNodeFunctionWrapperHeader("nodejs_tools_for_visual_studio_hidden_module_body", _diskBuffer.GetFilePath());
        }

        /// <summary>
        /// Gets the FileNode for the this projection buffer if one exists.
        /// </summary>
        internal NodejsFileNode GetFileNode() {
            NodejsFileNode node = null;
            IVsRunningDocumentTable rdt = NodejsPackage.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            IVsHierarchy hierarchy;
            uint itemid;
            IntPtr docData = IntPtr.Zero;
            uint docCookie;
            try {
                ErrorHandler.ThrowOnFailure(
                    rdt.FindAndLockDocument(
                        (uint)_VSRDTFLAGS.RDT_NoLock,
                        _diskBuffer.GetFilePath(),
                        out hierarchy,
                        out itemid,
                        out docData,
                        out docCookie
                    )
                );

                var nodejsProject = hierarchy.GetProject().GetNodeProject();
                if (nodejsProject != null) {
                    node = nodejsProject.FindNodeByFullPath(_diskBuffer.GetFilePath()) as NodejsFileNode;
                }
            } finally {
                if (docData != IntPtr.Zero) {
                    Marshal.Release(docData);
                }
            }
            return node;
        }

        /// <summary>
        /// Gets the header function that we wrap code in to make it into the Node.js like
        /// environment for intellisense.
        /// </summary>
        /// <param name="functionName">The name of the outer function</param>
        /// <param name="filename">The .js file which we are emitting, for setting __filename and __dirname.</param>
        /// <param name="localFilenames">True if __filename and __dirname are defined as locals, false to define as globals.  
        /// 
        /// When we emit into the require() body we want these to be locals so we don't have the different
        /// modules trampling on each other.
        /// </param>
        /// <returns></returns>
        internal static string GetNodeFunctionWrapperHeader(string functionName, string filename) {
            // __filename, _dirname http://nodejs.org/api/globals.html#globals_filename
            return "function " + functionName + "() {\r\n" +
                GetFileNameAssignment(filename) +
                GetDirectoryNameAssignment(filename) +
                "var exports = {};\r\n" +
                "var module = {};\r\n" +
                "module.exports = exports;\r\n" ;
        }

        internal static string GetFileNameAssignment(string filename) {
            return "__filename = \"" + filename.Replace("\\", "\\\\") + "\";\r\n";
        }

        internal static string GetDirectoryNameAssignment(string filename) {
            if (String.IsNullOrWhiteSpace(filename)) {
                return "";
            }

            string dirname;
            
            try {
                dirname = Path.GetDirectoryName(filename);
            } catch (PathTooLongException) {
                //We should always have a \ present in the path we get
                //  to be safe we'll check and fall back to supplying the full path
                int indexOfBackslash = filename.LastIndexOf('\\');                
                dirname = indexOfBackslash > 0 ? filename.Substring(0, indexOfBackslash) : filename;            
            }

            return "__dirname = \"" + dirname.Replace("\\", "\\\\") + "\";\r\n";
        }

        internal static string TrailingText {
            get {
                // 
                return "\r\nreturn module.exports;\r\n}";
            }
        }

        private IProjectionBuffer CreateElisionBuffer(IProjectionBufferFactoryService bufferFactory) {
            var res = bufferFactory.CreateProjectionBuffer(
                null,
                new object[] { 
                    new CustomTrackingSpan(
                        _projBuffer.CurrentSnapshot,
                        this
                    )
                },
                ProjectionBufferOptions.None
            );
            return res;
        }

        public IProjectionBuffer ProjectionBuffer {
            get {
                return _projBuffer;
            }
        }

        public IProjectionBuffer EllisionBuffer {
            get {
                return _elisionBuffer;
            }
        }

        public ITextBuffer DiskBuffer {
            get {
                return _diskBuffer;
            }
        }
    }

    class ProjectionBufferBoundsEdit {
        public readonly int Start;
        public ProjectionBufferBoundsEdit(int start) {
            Start = start;
        }
    }
}
