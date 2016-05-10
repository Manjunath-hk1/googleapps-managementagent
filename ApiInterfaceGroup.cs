﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lithnet.GoogleApps.MA
{
    using Google.Apis.Admin.Directory.directory_v1.Data;
    using MetadirectoryServices;
    using Microsoft.MetadirectoryServices;
    using User = ManagedObjects.User;

    public class ApiInterfaceGroup : ApiInterface
    {
        private static MASchemaType maType = SchemaBuilder.GetUserSchema();

        public ApiInterfaceGroup()
        {
            this.Api = "group";
        }

        public override bool IsPrimary => true;

        public override object CreateInstance(CSEntryChange csentry)
        {
            return new GoogleGroup();
        }

        public override object GetInstance(CSEntryChange csentry)
        {
            return GroupRequestFactory.Get(csentry.GetAnchorValueOrDefault<string>("id") ?? csentry.DN);
        }

        public override void DeleteInstance(CSEntryChange csentry)
        {
            GroupRequestFactory.Delete(csentry.GetAnchorValueOrDefault<string>("id") ?? csentry.DN);
        }

        public override IList<AttributeChange> ApplyChanges(CSEntryChange csentry, SchemaType type, object target, bool patch = false)
        {
            bool hasChanged = false;

            foreach (IMASchemaAttribute typeDef in ApiInterfaceGroup.maType.Attributes.Where(t => t.Api == this.Api))
            {
                if (typeDef.UpdateField(csentry, target))
                {
                    hasChanged = true;
                }
            }

            if (!hasChanged)
            {
                return new List<AttributeChange>();
            }

            Group result;

            if (csentry.ObjectModificationType == ObjectModificationType.Add)
            {
                result = GroupRequestFactory.Add((Group)target);
            }
            else if (csentry.ObjectModificationType == ObjectModificationType.Replace || csentry.ObjectModificationType == ObjectModificationType.Update)
            {
                if (patch)
                {
                    result = GroupRequestFactory.Patch(this.GetAnchorValue(target), (Group)target);
                }
                else
                {
                    result = GroupRequestFactory.Update(this.GetAnchorValue(target), (Group) target);
                }
            }
            else
            {
                throw new InvalidOperationException();
            }

            return this.GetChanges(csentry.ObjectModificationType, type, result);
        }

        public override IList<AttributeChange> GetChanges(ObjectModificationType modType, SchemaType type, object source)
        {
            List<AttributeChange> attributeChanges = new List<AttributeChange>();

            foreach (IMASchemaAttribute typeDef in ApiInterfaceGroup.maType.Attributes.Where(t => t.Api == this.Api))
            {
                if (type.HasAttribute(typeDef.AttributeName))
                {
                    attributeChanges.AddRange(typeDef.CreateAttributeChanges(modType, source));
                }
            }

            return attributeChanges;
        }

        public override string GetAnchorValue(object target)
        {
            return ((Group)target).Id;
        }

        public override string GetDNValue(object target)
        {
            return ((Group)target).Email;
        }
    }
}
