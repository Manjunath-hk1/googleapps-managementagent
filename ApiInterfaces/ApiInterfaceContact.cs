﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lithnet.GoogleApps.MA
{
    using Google.Contacts;
    using Google.GData.Contacts;
    using Google.GData.Extensions;
    using MetadirectoryServices;
    using Microsoft.MetadirectoryServices;

    internal class ApiInterfaceContact : IApiInterfaceObject
    {
        private const string DNAttributeName = "lithnet-google-ma-dn";
        private static ApiInterfaceKeyedCollection internalInterfaces;

        private string domain;

        static ApiInterfaceContact()
        {
            ApiInterfaceContact.internalInterfaces = new ApiInterfaceKeyedCollection();
        }

        public ApiInterfaceContact(string domain)
        {
            this.domain = domain;
        }

        public string Api => "contact";


        public object CreateInstance(CSEntryChange csentry)
        {
            return new ContactEntry();
        }

        public object GetInstance(CSEntryChange csentry)
        {
            return ContactRequestFactory.GetContact(csentry.GetAnchorValueOrDefault<string>("id"));
        }

        public void DeleteInstance(CSEntryChange csentry)
        {
            ContactRequestFactory.DeleteContact(csentry.GetAnchorValueOrDefault<string>("id"));
        }

        public IList<AttributeChange> ApplyChanges(CSEntryChange csentry, SchemaType type, object target, bool patch = false)
        {
            bool hasChanged = false;
            List<AttributeChange> changes = new List<AttributeChange>();
            ContactEntry obj = (ContactEntry)target;

            if (this.SetDNValue(csentry, obj))
            {
                hasChanged = true;
            }

            foreach (IMASchemaAttribute typeDef in ManagementAgent.Schema[SchemaConstants.Contact].Attributes.Where(t => t.Api == this.Api))
            {
                if (typeDef.UpdateField(csentry, obj))
                {
                    hasChanged = true;
                }
            }

            if (hasChanged)
            {
                ContactEntry result;

                if (csentry.ObjectModificationType == ObjectModificationType.Add)
                {
                    result = ContactRequestFactory.CreateContact(obj, this.domain);
                }
                else if (csentry.ObjectModificationType == ObjectModificationType.Replace || csentry.ObjectModificationType == ObjectModificationType.Update)
                {
                    if (patch)
                    {
                        throw new InvalidOperationException();
                    }
                    else
                    {
                        result = ContactRequestFactory.UpdateContact(obj);
                    }
                }
                else
                {
                    throw new InvalidOperationException();
                }

                changes.AddRange(this.GetChanges(csentry.DN, csentry.ObjectModificationType, type, result));
            }

            foreach (IApiInterface i in ApiInterfaceContact.internalInterfaces)
            {
                changes.AddRange(i.ApplyChanges(csentry, type, target, patch));
            }

            return changes;
        }

        public IList<AttributeChange> GetChanges(string dn, ObjectModificationType modType, SchemaType type, object source)
        {
            List<AttributeChange> attributeChanges = new List<AttributeChange>();

            ContactEntry entry = source as ContactEntry;

            if (entry == null)
            {
                throw new InvalidOperationException();
            }

            foreach (IMASchemaAttribute typeDef in ManagementAgent.Schema[SchemaConstants.Contact].Attributes.Where(t => t.Api == this.Api))
            {
                foreach (AttributeChange change in typeDef.CreateAttributeChanges(dn, modType, entry))
                {
                    if (type.HasAttribute(change.Name))
                    {
                        attributeChanges.Add(change);
                    }
                }
            }

            foreach (IApiInterface i in ApiInterfaceContact.internalInterfaces)
            {
                attributeChanges.AddRange(i.GetChanges(dn, modType, type, source));
            }

            return attributeChanges;
        }

        public string GetAnchorValue(object target)
        {
            ContactEntry contactEntry = target as ContactEntry;

            if (contactEntry != null)
            {
                return contactEntry.Id.ToString();
            }

            Contact contact = target as Contact;

            if (contact != null)
            {
                return contact.Id;
            }

            throw new InvalidOperationException();
        }

        public string GetDNValue(object target)
        {
            ContactEntry contactEntry = target as ContactEntry;

            if (contactEntry == null)
            {
                throw new InvalidOperationException();
            }

            if (contactEntry.ExtendedProperties.Count > 0)
            {
                ExtendedProperty dn = contactEntry.ExtendedProperties.FirstOrDefault(t => t.Name == ApiInterfaceContact.DNAttributeName);

                if (!string.IsNullOrEmpty(dn?.Value))
                {
                    return dn.Value;
                }
            }

            return "contact:" + contactEntry.PrimaryEmail.Address;
        }

        public bool SetDNValue(CSEntryChange csentry, ContactEntry e)
        {
            if (csentry.ObjectModificationType != ObjectModificationType.Replace && csentry.ObjectModificationType != ObjectModificationType.Update)
            {
                return false;
            }

            string newDN = csentry.GetNewDNOrDefault<string>();

            if (newDN == null)
            {
                return false;
            }

            ExtendedProperty dn = e.ExtendedProperties.FirstOrDefault(t => t.Name == ApiInterfaceContact.DNAttributeName);

            if (dn == null)
            {
                dn = new ExtendedProperty {Name = ApiInterfaceContact.DNAttributeName};
                e.ExtendedProperties.Add(dn);
            }

            dn.Value = newDN;
            return true;
        }
    }
}
