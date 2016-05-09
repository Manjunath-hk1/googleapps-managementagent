using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Text;
using System.Collections.Specialized;
using Microsoft.MetadirectoryServices;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Lithnet.Logging;
using System.Threading;
using System.Collections.Concurrent;
using Lithnet.GoogleApps.ManagedObjects;
using System.Diagnostics;
using Lithnet.MetadirectoryServices;

namespace Lithnet.GoogleApps.MA
{
    public class ManagementAgent :
        IMAExtensible2CallExport,
        IMAExtensible2CallImport,
        IMAExtensible2GetSchema,
        IMAExtensible2GetCapabilities,
        IMAExtensible2GetParameters,
        IMAExtensible2Password
    {
        private const string deltafile = "lithnet.googleapps.ma.delta.xml";

        private OpenImportConnectionRunStep importRunStep;

        private OpenExportConnectionRunStep exportRunStep;

        private Stopwatch timer;

        private int opCount;

        private Task importUsersTask;

        private Task importGroupsTask;

        private Schema operationSchemaTypes;

        public ManagementAgent()
        {
            Logger.LogPath = @"D:\MAData\MonashGoogleApps\ma.log";
        }
        
        public int ExportDefaultPageSize
        {
            get { return 100; }
        }

        public int ExportMaxPageSize
        {
            get { return 9999; }
        }

        public int ImportDefaultPageSize
        {
            get { return 100; }
        }

        public int ImportMaxPageSize
        {
            get { return 9999; }
        }

        public string DeltaPath { get; set; }

        public MACapabilities Capabilities
        {
            get
            {
                MACapabilities capabilities = new MACapabilities();
                capabilities.ConcurrentOperation = false;
                capabilities.DeleteAddAsReplace = true;
                capabilities.DeltaImport = true;
                capabilities.DistinguishedNameStyle = MADistinguishedNameStyle.Generic;
                capabilities.ExportPasswordInFirstPass = false;
                capabilities.ExportType = MAExportType.AttributeUpdate;
                capabilities.FullExport = false;
                capabilities.IsDNAsAnchor = false;
                capabilities.NoReferenceValuesInFirstExport = false;
                capabilities.Normalizations = MANormalizations.None;
                capabilities.ObjectConfirmation = MAObjectConfirmation.Normal;
                capabilities.ObjectRename = true;

                return capabilities;
            }
        }

        internal IManagementAgentParameters Configuration { get; private set; }

        public void OpenExportConnection(System.Collections.ObjectModel.KeyedCollection<string, ConfigParameter> configParameters, Schema types, OpenExportConnectionRunStep exportRunStep)
        {
            Logger.WriteLine("Opening export connection");
            this.timer = new Stopwatch();
            this.Configuration = new ManagementAgentParameters(configParameters);
            this.exportRunStep = exportRunStep;
            this.operationSchemaTypes = types;
            this.DeltaPath = Path.Combine(@"C:\Program Files\Microsoft Forefront Identity Manager\2010\Synchronization Service\MaData\g2", deltafile);

            CSEntryChangeQueue.LoadQueue(this.DeltaPath);
            ConnectionPools.InitializePools(this.Configuration.Credentials, this.Configuration.ExportThreadCount, this.Configuration.ExportThreadCount);
            GroupMembership.GetInternalDomains(this.Configuration.CustomerID);
            this.timer.Start();

        }

        public PutExportEntriesResults PutExportEntries(IList<CSEntryChange> csentries)
        {
            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = this.Configuration.ExportThreadCount;
            PutExportEntriesResults results = new PutExportEntriesResults();

            Parallel.ForEach(csentries, po, (csentry) =>
            {
                try
                {
                    Interlocked.Increment(ref opCount);
                    Logger.StartThreadLog();
                    Logger.WriteSeparatorLine('-');
                    Logger.WriteLine("Starting export {0} for user {1}", csentry.ObjectModificationType, csentry.DN);
                    SchemaType type = this.operationSchemaTypes.Types[csentry.ObjectType];
                    CSEntryChangeResult result = CSEntryChangeFactory.PutCSEntryChange(csentry, this.Configuration, type);
                    lock (results)
                    {
                        results.CSEntryChangeResults.Add(result);
                    }
                }
                catch (AggregateException ex)
                {
                    Logger.WriteLine("An unexpected error occurred while processing {0}", csentry.DN);
                    Logger.WriteException(ex);
                    CSEntryChangeResult result = CSEntryChangeResult.Create(csentry.Identifier, null, MAExportError.ExportErrorCustomContinueRun, ex.InnerException.Message, ex.InnerException.StackTrace);
                    lock (results)
                    {
                        results.CSEntryChangeResults.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("An unexpected error occurred while processing {0}", csentry.DN);
                    Logger.WriteException(ex);
                    CSEntryChangeResult result = CSEntryChangeResult.Create(csentry.Identifier, null, MAExportError.ExportErrorCustomContinueRun, ex.Message, ex.StackTrace);
                    lock (results)
                    {
                        results.CSEntryChangeResults.Add(result);
                    }
                }
                finally
                {
                    Logger.WriteSeparatorLine('-');
                    Logger.EndThreadLog();
                }
            });

            return results;
        }

        public void CloseExportConnection(CloseExportConnectionRunStep exportRunStep)
        {
            Logger.WriteLine("Closing export connection: {0}", exportRunStep.Reason);
            this.timer.Stop();

            try
            {
                Logger.WriteLine("Writing {0} delta entries to file", CSEntryChangeQueue.Count);
                CSEntryChangeQueue.SaveQueue(this.DeltaPath, this.operationSchemaTypes);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("An error occurred while saving the delta file");
                Logger.WriteException(ex);
                throw;
            }

            Logger.WriteSeparatorLine('*');
            Logger.WriteLine("Operation statistics");
            Logger.WriteLine("Export objects: {0}", opCount);
            Logger.WriteLine("Operation time: {0}", timer.Elapsed);
            Logger.WriteLine("Ops/sec: {0:N3}", opCount / timer.Elapsed.TotalSeconds);
            Logger.WriteSeparatorLine('*');

        }

        public OpenImportConnectionResults OpenImportConnection(KeyedCollection<string, ConfigParameter> configParameters, Schema types, OpenImportConnectionRunStep importRunStep)
        {
            this.importRunStep = importRunStep;
            this.operationSchemaTypes = types;
            this.timer = new Stopwatch();
            this.Configuration = new ManagementAgentParameters(configParameters);
            this.DeltaPath = Path.Combine(@"C:\Program Files\Microsoft Forefront Identity Manager\2010\Synchronization Service\MaData\g2", deltafile);

            Logger.WriteLine("Opening import connection. Page size {0}", this.importRunStep.PageSize);

            if (this.importRunStep.ImportType == OperationType.Delta)
            {
                CSEntryChangeQueue.LoadQueue(this.DeltaPath);
                Logger.WriteLine("Delta full import from file started. {0} entries to import", CSEntryChangeQueue.Count);
            }
            else
            {
                OpenImportConnectionFull(types);

                Logger.WriteLine("Background full import from Google started");
            }

            timer.Start();
            return new OpenImportConnectionResults("<placeholder>");
        }

        private void OpenImportConnectionFull(Schema types)
        {
            this.importCollection = new BlockingCollection<object>();

            ConnectionPools.InitializePools(this.Configuration.Credentials, this.Configuration.GroupMembersImportThreadCount + 1, this.Configuration.GroupSettingsImportThreadCount);
            GroupMembership.GetInternalDomains(this.Configuration.CustomerID);

            if (this.operationSchemaTypes.Types.Contains("user") || this.operationSchemaTypes.Types.Contains("advancedUser"))
            {
                this.SetupUserImportTask(types);
                Logger.WriteLine("User import task setup complete");
            }
            else
            {
                this.UserImportTaskComplete = true;
            }

            if (this.operationSchemaTypes.Types.Contains("group"))
            {
                this.SetupGroupsImportTask();
                Logger.WriteLine("Group import task setup complete");
            }
            else
            {
                this.GroupImportTaskComplete = true;
            }

            if (this.importUsersTask != null)
            {
                this.importUsersTask.Start();
            }

            if (this.importGroupsTask != null)
            {
                this.importGroupsTask.Start();
            }
        }

        private void ThrowOnFaultedTask()
        {
            if (this.importGroupsTask != null)
            {
                if (this.importGroupsTask.IsFaulted)
                {
                    throw this.importGroupsTask.Exception;
                }
            }

            if (this.importUsersTask != null)
            {
                if (this.importUsersTask.IsFaulted)
                {
                    throw this.importUsersTask.Exception;
                }
            }
        }

        private void SetupGroupsImportTask()
        {
            bool membersRequired = ManagementAgentSchema.IsGroupMembershipRequired(this.operationSchemaTypes.Types["group"]);
            bool settingsRequred = ManagementAgentSchema.IsGroupSettingsRequired(this.operationSchemaTypes.Types["group"]);

            string groupFields = string.Format("groups({0}), nextPageToken", ManagementAgentSchema.ConvertTypesToFieldParameter("group", this.operationSchemaTypes.Types["group"]));
            string groupSettingsFields = ManagementAgentSchema.ConvertTypesToFieldParameter("groupSettings", this.operationSchemaTypes.Types["group"]);
            GroupRequestFactory.MemberThreads = this.Configuration.GroupMembersImportThreadCount;
            GroupRequestFactory.SettingsThreads = this.Configuration.GroupSettingsImportThreadCount;

            this.importGroupsTask = new Task(() =>
                {
                    Logger.WriteLine("Starting group import task");
                    Logger.WriteLine("Requesting group fields: " + groupFields);
                    Logger.WriteLine("Requesting group settings fields: " + groupSettingsFields);

                    Logger.WriteLine("Requesting settings: " + settingsRequred.ToString());
                    Logger.WriteLine("Requesting members: " + membersRequired.ToString());

                    GroupRequestFactory.ImportGroups(this.Configuration.CustomerID, membersRequired, settingsRequred, groupFields, groupSettingsFields, this.importCollection);

                    Logger.WriteLine("Groups import task complete");

                    this.GroupImportTaskComplete = true;

                    lock (this.importCollection)
                    {
                        if (this.UserImportTaskComplete && this.GroupImportTaskComplete)
                        {
                            this.importCollection.CompleteAdding();
                        }
                    }
                });
        }

        private void SetupUserImportTask(Schema types)
        {
            HashSet<string> attributeNames = new HashSet<string>();

            if (this.operationSchemaTypes.Types.Contains("user"))
            {
                foreach (string name in types.Types["user"].Attributes.Select(t => t.Name))
                {
                    attributeNames.Add(name);
                }
            }

            if (this.operationSchemaTypes.Types.Contains("advancedUser"))
            {
                foreach (string name in this.operationSchemaTypes.Types["advancedUser"].Attributes.Select(t => t.Name))
                {
                    attributeNames.Add(name);
                }
            }

            string fields = string.Format("users({0}),nextPageToken", ManagementAgentSchema.ConvertTypesToFieldParameter("user", attributeNames));

            this.importUsersTask = new Task(() =>
            {
                Logger.WriteLine("Starting user import task");
                Logger.WriteLine("Requesting fields: " + fields);
                UserRequestFactory.StartImport(this.Configuration.CustomerID, fields, this.importCollection);
                Logger.WriteLine("User import task complete");

                this.UserImportTaskComplete = true;

                lock (this.importCollection)
                {
                    if (this.UserImportTaskComplete && this.GroupImportTaskComplete)
                    {
                        this.importCollection.CompleteAdding();
                    }
                }
            });
        }

        private bool UserImportTaskComplete;

        private bool GroupImportTaskComplete;

        private BlockingCollection<object> importCollection;

        public GetImportEntriesResults GetImportEntries(GetImportEntriesRunStep importRunStep)
        {
            GetImportEntriesResults results;

            if (this.importRunStep.ImportType == OperationType.Full)
            {
                results = this.GetImportEntriesFull();
            }
            else if (this.importRunStep.ImportType == OperationType.Delta)
            {
                results = GetImportEntriesDelta();
            }
            else
            {
                throw new NotSupportedException();
            }

            return results;
        }

        private GetImportEntriesResults GetImportEntriesDelta()
        {
            GetImportEntriesResults results = new GetImportEntriesResults();
            results.CSEntries = new List<CSEntryChange>();

            int count = 0;

            while (CSEntryChangeQueue.Count > 0 && (count < this.importRunStep.PageSize))
            {
                Interlocked.Increment(ref opCount);
                results.CSEntries.Add(CSEntryChangeQueue.Take());
                count++;
            }

            results.MoreToImport = CSEntryChangeQueue.Count > 0;

            return results;
        }

        private GetImportEntriesResults GetImportEntriesFull()
        {
            GetImportEntriesResults results = new GetImportEntriesResults();
            results.CSEntries = new List<CSEntryChange>();
            //Logger.WriteLine("Import batch starting for {0} objects", this.importRunStep.PageSize);

            for (int i = 0; i < this.importRunStep.PageSize; i++)
            {
                this.ThrowOnFaultedTask();

                if (this.importCollection.IsCompleted)
                {
                    break;
                }

                object item;

                if (!this.importCollection.TryTake(out item))
                {
                    Thread.Sleep(25);
                    continue;
                }

                Interlocked.Increment(ref opCount);

                User user = item as User;

                if (user != null)
                {
                    if (!string.IsNullOrWhiteSpace(this.Configuration.UserRegexFilter))
                    {
                        if (!Regex.IsMatch(user.PrimaryEmail, this.Configuration.UserRegexFilter, RegexOptions.IgnoreCase))
                        {
                            i--;
                            continue;
                        }
                    }

                    results.CSEntries.Add(CSEntryChangeFactoryUser.UserToCSEntryChange(user, this.Configuration, this.operationSchemaTypes));
                    continue;
                }

                GoogleGroup group = item as GoogleGroup;

                if (group == null)
                {
                    throw new NotSupportedException("The object enumeration returned an unsupported type: " + group.GetType().Name);
                }

                if (!string.IsNullOrWhiteSpace(this.Configuration.GroupRegexFilter))
                {
                    if (!Regex.IsMatch(group.Group.Email, this.Configuration.GroupRegexFilter, RegexOptions.IgnoreCase))
                    {
                        i--;
                        continue;
                    }
                }

                if (this.Configuration.ExcludeUserCreated)
                {
                    if (!group.Group.AdminCreated.HasValue || !group.Group.AdminCreated.Value)
                    {
                        i--;
                        continue;
                    }
                }

                results.CSEntries.Add(GetCSEntryForGroup(group));
                continue;
            }

            results.MoreToImport = !this.importCollection.IsCompleted;

            if (results.MoreToImport && results.CSEntries.Count == 0)
            {
                Thread.Sleep(1000);
            }

            Logger.WriteLine("Import page complete. Returning {0} objects to sync engine", results.CSEntries.Count);
            return results;
        }

        private CSEntryChange GetCSEntryForGroup(GoogleGroup group)
        {
            CSEntryChange csentry;

            if (group.Errors.Count > 0)
            {
                csentry = CSEntryChange.Create();
                csentry.ObjectType = "group";
                csentry.ObjectModificationType = ObjectModificationType.Add;
                csentry.DN = group.Group.Email;
                csentry.ErrorCodeImport = MAImportError.ImportErrorCustomContinueRun;
                csentry.ErrorDetail = group.Errors.First().StackTrace;
                csentry.ErrorName = group.Errors.First().Message;
            }
            else
            {
                csentry = CSEntryChangeFactoryGroup.GroupToCSE(group, this.Configuration, this.operationSchemaTypes);
            }
            return csentry;
        }

        public CloseImportConnectionResults CloseImportConnection(CloseImportConnectionRunStep importRunStep)
        {
            Logger.WriteLine("Closing import connection: {0}", importRunStep.Reason);
            try
            {
                if (this.importRunStep.ImportType == OperationType.Full)
                {
                    CSEntryChangeQueue.Clear();
                    CSEntryChangeQueue.SaveQueue(this.DeltaPath, this.operationSchemaTypes);
                    Logger.WriteLine("Cleared delta file");

                }
                else
                {
                    Logger.WriteLine("Writing {0} delta entries to file", CSEntryChangeQueue.Count);
                    CSEntryChangeQueue.SaveQueue(this.DeltaPath, this.operationSchemaTypes);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("An unexpected error occured");
                Logger.WriteException(ex);
                throw;
            }
            Logger.WriteSeparatorLine('*');
            Logger.WriteLine("Operation statistics");
            Logger.WriteLine("Import objects: {0}", opCount);
            Logger.WriteLine("Operation time: {0}", timer.Elapsed);
            Logger.WriteLine("Ops/sec: {0:N3}", opCount / timer.Elapsed.TotalSeconds);
            Logger.WriteSeparatorLine('*');

            return new CloseImportConnectionResults(null);
        }

        public Schema GetSchema(KeyedCollection<string, ConfigParameter> configParameters)
        {
            this.Configuration = new ManagementAgentParameters(configParameters);

            ConnectionPools.InitializePools(this.Configuration.Credentials, this.Configuration.GroupMembersImportThreadCount + 1, this.Configuration.GroupSettingsImportThreadCount);

            return ManagementAgentSchema.GetSchema(this.Configuration);
        }

        public IList<ConfigParameterDefinition> GetConfigParameters(KeyedCollection<string, ConfigParameter> configParameters, ConfigParameterPage page)
        {
            return ManagementAgentParameters.GetParameters(configParameters, page);
        }

        public ParameterValidationResult ValidateConfigParameters(KeyedCollection<string, ConfigParameter> configParameters, ConfigParameterPage page)
        {
            ManagementAgentParameters parameters = new ManagementAgentParameters(configParameters);
            return parameters.ValidateParameters(page);
        }

        public ConnectionSecurityLevel GetConnectionSecurityLevel()
        {
            return ConnectionSecurityLevel.Secure;
        }

        public void OpenPasswordConnection(KeyedCollection<string, ConfigParameter> configParameters, Partition partition)
        {
            this.Configuration = new ManagementAgentParameters(configParameters);
            ConnectionPools.InitializePools(this.Configuration.Credentials, 1, 1);
        }

        public void SetPassword(CSEntry csentry, System.Security.SecureString newPassword, PasswordOptions options)
        {
            if (options == PasswordOptions.ValidatePassword)
            {
                return;
            }

            try
            {
                UserRequestFactory.SetPassword(csentry.DN.ToString(), newPassword.ConvertToUnsecureString());
                Logger.WriteLine("Set password for {0}", csentry.DN.ToString());
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Error setting password for {0}", csentry.DN.ToString());
                Logger.WriteException(ex);
                throw;
            }
        }

        public void ChangePassword(CSEntry csentry, System.Security.SecureString oldPassword, System.Security.SecureString newPassword)
        {
            try
            {
                UserRequestFactory.SetPassword(csentry.DN.ToString(), newPassword.ConvertToUnsecureString());
                Logger.WriteLine("Changed password for {0}", csentry.DN.ToString());
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Error changing password for {0}", csentry.DN.ToString());
                Logger.WriteException(ex);
                throw;
            }
        }

        public void ClosePasswordConnection()
        {
        }
    }
}