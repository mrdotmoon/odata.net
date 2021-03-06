﻿//---------------------------------------------------------------------
// <copyright file="ODataAvroOutputContext.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

#if ENABLE_AVRO
namespace Microsoft.Test.OData.PluggableFormat.Avro
{
    using System.IO;
    using System.Text;
    using Microsoft.OData.Core;
    using Microsoft.OData.Edm;

    internal class ODataAvroOutputContext : ODataOutputContext
    {
        private Stream outputStream;
        public AvroWriter AvroWriter { get; private set; }

        internal ODataAvroOutputContext(
             ODataFormat format,
             Stream messageStream,
             Encoding encoding,
             ODataMessageWriterSettings messageWriterSettings,
             bool writingResponse,
             bool synchronous,
             IEdmModel model,
             IODataUrlResolver urlResolver)
            : base(format, messageWriterSettings, writingResponse, synchronous, model, urlResolver)
        {
            this.outputStream = messageStream;
            this.AvroWriter = new AvroWriter(new StreamWrapper(outputStream));
        }

        public override ODataWriter CreateODataEntryWriter(IEdmNavigationSource navigationSource, IEdmEntityType entityType)
        {
            return new ODataAvroWriter(this, value => this.AvroWriter.Write(value), this.AvroWriter.UpdateSchema(null, entityType), false);
        }

        public override ODataWriter CreateODataFeedWriter(IEdmEntitySetBase entitySet, IEdmEntityType entityType)
        {
            return new ODataAvroWriter(this, value => this.AvroWriter.Write(value), this.AvroWriter.UpdateSchema(null, entityType, true), true);
        }

        public override ODataCollectionWriter CreateODataCollectionWriter(IEdmTypeReference itemTypeReference)
        {
            return new ODataAvroCollectionWriter(this, null);
        }

        public override ODataParameterWriter CreateODataParameterWriter(IEdmOperation operation)
        {
            return new ODataAvroParameterWriter(this, operation);
        }

        public override void WriteProperty(ODataProperty property)
        {
            var schema = this.AvroWriter.UpdateSchema(property.Value, null);
            var obj = ODataAvroConvert.FromODataObject(property.Value, schema);
            this.AvroWriter.Write(obj);
            this.Flush();
        }

        public override void WriteError(ODataError error, bool includeDebugInformation)
        {
            var schema = this.AvroWriter.UpdateSchema(error, null);
            var obj = ODataAvroConvert.FromODataObject(error, schema);
            this.AvroWriter.Write(obj);
            this.Flush();
        }

        internal void Flush()
        {
            this.AvroWriter.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (this.AvroWriter != null)
                    {
                        this.AvroWriter.Flush();
                        this.AvroWriter.Dispose();
                    }

                    if (this.outputStream != null)
                    {
                        this.outputStream.Flush();
                        this.outputStream.Dispose();
                    }

                }
                finally
                {
                    this.AvroWriter = null;
                    this.outputStream = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
#endif