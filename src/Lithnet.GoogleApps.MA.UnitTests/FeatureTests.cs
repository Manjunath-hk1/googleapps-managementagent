﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Google.Apis.Admin.Directory.directory_v1.Data;
using Lithnet.MetadirectoryServices;
using Microsoft.MetadirectoryServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lithnet.GoogleApps.MA.UnitTests
{
    [TestClass]
    public class FeatureTests
    {
        [TestMethod]
        public void GetFeatures()
        {
            foreach (Feature item in ResourceRequestFactory.GetFeatures("my_customer"))
            {
                {
                    Trace.WriteLine(item.Name);
                }
            }
        }

        [TestMethod]
        public void GetFeaturesViaApiInterface()
        {
            MASchemaType s = UnitTestControl.Schema[SchemaConstants.Feature];

            ApiInterfaceFeature u = new ApiInterfaceFeature("my_customer", s);

            BlockingCollection<object> items = new BlockingCollection<object>();

            u.GetItems(UnitTestControl.TestParameters, UnitTestControl.MmsSchema, items).Wait();

            foreach (CSEntryChange item in items.OfType<CSEntryChange>())
            {
                Assert.AreEqual(MAImportError.Success, item.ErrorCodeImport);
            }

            Assert.AreNotEqual(0, items.Count);
        }

        [TestMethod]
        public void CreateFeature()
        {
            CSEntryChange cs = CSEntryChange.Create();
            cs.ObjectModificationType = ObjectModificationType.Add;
            cs.DN = Guid.NewGuid().ToString("n");
            cs.ObjectType = SchemaConstants.Feature;

            cs.AttributeChanges.Add(AttributeChange.CreateAttributeAdd("name", "My feature"));

            string id = null;

            try
            {
                CSEntryChangeResult result = ExportProcessor.PutCSEntryChange(cs, UnitTestControl.Schema.GetSchema().Types[SchemaConstants.Feature], UnitTestControl.TestParameters);

                if (result.ErrorCode != MAExportError.Success)
                {
                    Assert.Fail(result.ErrorName);
                }

                id = result.AnchorAttributes["id"].GetValueAdd<string>();

                Thread.Sleep(UnitTestControl.PostGoogleOperationSleepInterval);

                Feature c = ResourceRequestFactory.GetFeature(UnitTestControl.TestParameters.CustomerID, id);
                Assert.AreEqual(cs.DN, c.Name);
                Assert.AreEqual(cs.DN, id);
            }
            finally
            {
                if (id != null)
                {
                    ResourceRequestFactory.DeleteFeature(UnitTestControl.TestParameters.CustomerID, id);
                }
            }
        }
    }
}