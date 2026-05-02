using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Store_Acquisition_Assistant
{
    public sealed partial class MainPage : Page
    {
        private const string StagingFolderTokenSetting = "StagingFolderToken";

        private PackageManager packageManager = new PackageManager();

        private sealed class ProductIdentity
        {
            public string Name { get; set; }
            public string Publisher { get; set; }
        }

        public MainPage()
        {
            this.InitializeComponent();
            this.GoButton.Click += GoButton_Click;
            this.ChangeFolderButton.Click += ChangeFolderButton_Click;
        }

        private async void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            StorageFolder folder = await PickStagingFolderAsync();
            if (folder == null)
            {
                UpdateStatus("Folder unchanged.");
                return;
            }

            SaveStagingFolder(folder);
            UpdateStatus("Staging folder saved.");
            OutputTextBlock.Text += $"[INFO] Staging folder: {folder.Path}\n";
        }

        private async void GoButton_Click(object sender, RoutedEventArgs e)
        {
            string productId = ProductIDTextBox.Text.Trim();

            if (string.IsNullOrEmpty(productId))
            {
                UpdateStatus("Error: Please enter a product ID");
                return;
            }

            try
            {
                UpdateStatus("Fetching product information from Microsoft Store...");
                OutputTextBlock.Text = "";

                // 1. Fetch Identity
                ProductIdentity identity = await FetchProductIdentityAsync(productId);

                if (identity == null || string.IsNullOrEmpty(identity.Name) || string.IsNullOrEmpty(identity.Publisher))
                {
                    UpdateStatus("Error: Could not extract Identity Name and Publisher from product data");
                    return;
                }

                IdentityTextBlock.Text = $"{identity.Name}\n{identity.Publisher}";
                OutputTextBlock.Text += $"[✓] Identity Name fetched: {identity.Name}\n";
                OutputTextBlock.Text += $"[✓] Identity Publisher fetched: {identity.Publisher}\n\n";

                // 2. Use remembered staging folder. First run asks once; later deploys reuse it.
                UpdateStatus("Preparing staging folder...");
                StorageFolder stagingFolder = await GetSavedStagingFolderAsync();
                if (stagingFolder == null)
                {
                    OutputTextBlock.Text += "[INFO] No saved staging folder. Opening folder picker...\n";
                    stagingFolder = await PickStagingFolderAsync();
                    if (stagingFolder == null)
                    {
                        UpdateStatus("Operation cancelled by user.");
                        return;
                    }

                    SaveStagingFolder(stagingFolder);
                }

                OutputTextBlock.Text += $"[INFO] Staging folder: {stagingFolder.Path}\n";

                await UninstallExistingPackageAsync(identity.Name);

                // 3. Clear and copy template files to staging folder
                UpdateStatus("Clearing staging folder...");
                await ClearFolderAsync(stagingFolder);
                OutputTextBlock.Text += "[✓] Staging folder cleared\n";

                UpdateStatus("Copying template files...");
                StorageFolder installedFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
                try
                {
                    StorageFolder newAppSource = await installedFolder.GetFolderAsync("newapp");
                    await CopyFolderAsync(newAppSource, stagingFolder);
                    OutputTextBlock.Text += "[✓] App template copied to staging folder\n";
                    OutputTextBlock.Text += "[INFO] Assets are self-contained in the staging folder\n";
                }
                catch (FileNotFoundException)
                {
                    OutputTextBlock.Text += "[!] Error: 'newapp' folder not found in installation directory.\n";
                    return;
                }

                // 4. Update Configuration Files
                UpdateStatus("Updating configuration files...");

                bool manifestUpdated = await UpdateAppxManifestAsync(stagingFolder, identity);
                if (manifestUpdated)
                    OutputTextBlock.Text += "[✓] Updated AppxManifest.xml Identity\n";
                else
                {
                    OutputTextBlock.Text += "[!] Warning: Could not update AppxManifest.xml\n";
                    return;
                }

                bool jsUpdated = await UpdateMainJsAsync(stagingFolder, productId);
                if (jsUpdated)
                    OutputTextBlock.Text += $"[✓] Updated main.js with Product ID: {productId}\n";

                // 5. Pre-Flight Check: Verify Assets exist
                // The manifest refs 'images\storelogo.png' etc. 
                // These should be in the newapp folder that was copied.
                try 
                {
                    var item = await stagingFolder.TryGetItemAsync("images");
                    if (item == null)
                    {
                        OutputTextBlock.Text += "[!] WARNING: 'images' folder missing in staging directory.\n";
                        OutputTextBlock.Text += "    Ensure 'images' folder exists in the 'newapp' project folder.\n";
                    }
                    else
                    {
                        OutputTextBlock.Text += "[✓] 'images' folder confirmed present.\n";
                    }
                }
                catch { /* Ignore check errors */ }

                // 6. Deploy from the picked staging folder. PowerShell loose registration uses this same location.
                UpdateStatus("Deploying app...");
                bool deployed = await DeployAppPackageAsync(stagingFolder, identity.Name);
                
                if (deployed)
                {
                    UpdateStatus("App deployed successfully!");
                    OutputTextBlock.Text += "\n[✓] Process complete!\n";
                    await LaunchPackageAsync(identity.Name);
                }
                else
                {
                    UpdateStatus("App deployment failed");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
                OutputTextBlock.Text += $"\nException: {ex.Message}\n{ex.StackTrace}";
            }
        }

        private async Task CopyFolderAsync(StorageFolder source, StorageFolder destination)
        {
            foreach (var file in await source.GetFilesAsync())
            {
                await file.CopyAsync(destination, file.Name, NameCollisionOption.ReplaceExisting);
            }
            foreach (var subFolder in await source.GetFoldersAsync())
            {
                var newSubFolder = await destination.CreateFolderAsync(subFolder.Name, CreationCollisionOption.OpenIfExists);
                await CopyFolderAsync(subFolder, newSubFolder);
            }
        }

        private async Task ClearFolderAsync(StorageFolder folder)
        {
            foreach (var file in await folder.GetFilesAsync())
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }

            foreach (var subFolder in await folder.GetFoldersAsync())
            {
                await subFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
        }

        private async Task<StorageFolder> GetSavedStagingFolderAsync()
        {
            object tokenValue = ApplicationData.Current.LocalSettings.Values[StagingFolderTokenSetting];
            string token = tokenValue as string;
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                return await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
            }
            catch
            {
                ApplicationData.Current.LocalSettings.Values.Remove(StagingFolderTokenSetting);
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                return null;
            }
        }

        private async Task<StorageFolder> PickStagingFolderAsync()
        {
            FolderPicker picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");
            picker.CommitButtonText = "Use Staging Folder";

            return await picker.PickSingleFolderAsync();
        }

        private void SaveStagingFolder(StorageFolder folder)
        {
            object oldTokenValue = ApplicationData.Current.LocalSettings.Values[StagingFolderTokenSetting];
            string oldToken = oldTokenValue as string;
            if (!string.IsNullOrEmpty(oldToken) && StorageApplicationPermissions.FutureAccessList.ContainsItem(oldToken))
                StorageApplicationPermissions.FutureAccessList.Remove(oldToken);

            string token = StorageApplicationPermissions.FutureAccessList.Add(folder);
            ApplicationData.Current.LocalSettings.Values[StagingFolderTokenSetting] = token;
        }

        private async Task UninstallExistingPackageAsync(string identityName)
        {
            Package existingPackage = packageManager.FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => p.Id.Name.Equals(identityName, StringComparison.OrdinalIgnoreCase));

            if (existingPackage == null)
            {
                OutputTextBlock.Text += "[INFO] Existing package not installed. Skipping uninstall.\n";
                return;
            }

            UpdateStatus("Uninstalling existing package...");
            OutputTextBlock.Text += $"[INFO] Removing package: {existingPackage.Id.FullName}\n";

            DeploymentResult result = await packageManager.RemovePackageAsync(existingPackage.Id.FullName);
            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                OutputTextBlock.Text += $"[INFO] Uninstall result: {result.ErrorText}\n";
                throw new InvalidOperationException(result.ErrorText);
            }

            OutputTextBlock.Text += "[✓] Existing package removed\n";
        }

        private async Task LaunchPackageAsync(string identityName)
        {
            try
            {
                Package package = packageManager.FindPackagesForUser(string.Empty)
                    .FirstOrDefault(p => p.Id.Name.Equals(identityName, StringComparison.OrdinalIgnoreCase));

                if (package == null)
                {
                    OutputTextBlock.Text += "[!] Could not find deployed package to launch.\n";
                    return;
                }

                IReadOnlyList<AppListEntry> entries = await package.GetAppListEntriesAsync();
                AppListEntry entry = entries.FirstOrDefault();
                if (entry == null)
                {
                    OutputTextBlock.Text += "[!] Deployed package has no app entry to launch.\n";
                    return;
                }

                bool launched = await entry.LaunchAsync();
                OutputTextBlock.Text += launched ? "[✓] App launched\n" : "[!] App launch returned false\n";
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Launch failed: {ex.Message}\n";
            }
        }

        private async Task<ProductIdentity> FetchProductIdentityAsync(string productId)
        {
            string url = $"https://displaycatalog.mp.microsoft.com/v7.0/products/{productId}/0010?fieldsTemplate=InstallAgent&market=US&languages=en-US,en,impartial";

            using (HttpClient client = new HttpClient())
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string content = await response.Content.ReadAsStringAsync();
                content = content.TrimStart('\uFEFF'); // Trim BOM

                JsonObject jsonObject = JsonObject.Parse(content);
                IJsonValue productValue;
                if (jsonObject.TryGetValue("Product", out productValue) && productValue.ValueType == JsonValueType.Object)
                {
                    JsonObject productObject = productValue.GetObject();
                    
                    ProductIdentity identity = new ProductIdentity();

                    // Check Properties
                    if (productObject.TryGetValue("Properties", out IJsonValue propsVal) && propsVal.ValueType == JsonValueType.Object)
                    {
                        var props = propsVal.GetObject();
                        if (props.TryGetValue("PackageIdentityName", out IJsonValue pid) && pid.ValueType == JsonValueType.String)
                            identity.Name = pid.GetString();
                        if (props.TryGetValue("PublisherCertificateName", out IJsonValue publisher) && publisher.ValueType == JsonValueType.String)
                            identity.Publisher = publisher.GetString();
                    }

                    // Check LocalizedProperties as fallback for older catalog payloads
                    if (productObject.TryGetValue("LocalizedProperties", out IJsonValue locVal) && locVal.ValueType == JsonValueType.Array)
                    {
                        var arr = locVal.GetArray();
                        if (arr.Count > 0)
                        {
                            var prop = arr.GetObjectAt(0);
                            if (string.IsNullOrEmpty(identity.Name)
                                && prop.TryGetValue("PackageIdentityName", out IJsonValue pid)
                                && pid.ValueType == JsonValueType.String)
                                identity.Name = pid.GetString();
                            if (string.IsNullOrEmpty(identity.Publisher)
                                && prop.TryGetValue("PublisherCertificateName", out IJsonValue publisher)
                                && publisher.ValueType == JsonValueType.String)
                                identity.Publisher = publisher.GetString();
                        }
                    }

                    return identity;
                }
                return null;
            }
        }

        private async Task<bool> UpdateAppxManifestAsync(StorageFolder workingFolder, ProductIdentity identity)
        {
            try
            {
                // Find manifest
                StorageFile manifestFile = await workingFolder.TryGetItemAsync("AppxManifest.template.xml") as StorageFile 
                                        ?? await workingFolder.TryGetItemAsync("AppxManifest.xml") as StorageFile;

                if (manifestFile == null) return false;

                string content = await FileIO.ReadTextAsync(manifestFile);
                XDocument doc = XDocument.Parse(content);
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

                // Update Identity values used to produce package family name
                var identityElement = doc.Descendants(ns + "Identity").FirstOrDefault();
                if (identityElement != null)
                {
                    identityElement.SetAttributeValue("Name", identity.Name);
                    identityElement.SetAttributeValue("Publisher", identity.Publisher);
                }

                // Save as AppxManifest.xml
                StorageFile newManifest = await workingFolder.CreateFileAsync("AppxManifest.xml", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(newManifest, doc.ToString());
                
                return true;
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error updating manifest: {ex.Message}\n";
                return false;
            }
        }

        private async Task<bool> UpdateMainJsAsync(StorageFolder workingFolder, string productId)
        {
            try
            {
                StorageFile jsFile = await workingFolder.GetFileAsync("main.js");
                string content = await FileIO.ReadTextAsync(jsFile);
                string updatedContent = content.Replace("ONESTOREID", productId);
                await FileIO.WriteTextAsync(jsFile, updatedContent);
                return true;
            }
            catch
            {
                // If main.js doesn't exist, it's not critical for manifest validation
                return false;
            }
        }

        private async Task<bool> DeployAppPackageAsync(StorageFolder deployFolder, string identityName)
        {
            try
            {
                string manifestPath = Path.Combine(deployFolder.Path, "AppxManifest.xml");
                OutputTextBlock.Text += $"[INFO] Deploy folder: {deployFolder.Path}\n";
                OutputTextBlock.Text += $"[INFO] Manifest: {manifestPath}\n";

                // Verify we can access the manifest file
                StorageFile manifestFile = await deployFolder.GetFileAsync("AppxManifest.xml");
                if (manifestFile == null)
                {
                    OutputTextBlock.Text += "[!] Cannot access AppxManifest.xml\n";
                    return false;
                }
                OutputTextBlock.Text += "[✓] AppxManifest.xml is accessible\n";

                // Try RegisterPackageByUriAsync with manifest file URI (what PowerShell uses)
                Uri manifestUri = new Uri(manifestPath);
                OutputTextBlock.Text += $"[INFO] Manifest URI: {manifestUri}\n";
                OutputTextBlock.Text += $"[INFO] Attempting RegisterPackageByUriAsync...\n";

                var options = new RegisterPackageOptions
                {
                    AllowUnsigned = true,
                    DeveloperMode = true,
                    StageInPlace = true
                };
                OutputTextBlock.Text += "[INFO] Options: AllowUnsigned=True, DeveloperMode=True, StageInPlace=True\n";

                var deploymentResult = await packageManager.RegisterPackageByUriAsync(manifestUri, options);

                if (deploymentResult.IsRegistered)
                {
                    OutputTextBlock.Text += "[✓] Package registered successfully!\n";
                    
                    try
                    {
                        var pkg = packageManager.FindPackagesForUser(string.Empty)
                            .FirstOrDefault(p => p.Id.Name.Equals(identityName, StringComparison.OrdinalIgnoreCase));
                        
                        if (pkg != null)
                            OutputTextBlock.Text += $"[✓] Package Family: {pkg.Id.FamilyName}\n";
                    }
                    catch { }

                    return true;
                }
                else
                {
                    OutputTextBlock.Text += $"[!] Deployment failed - IsRegistered is false\n";
                    if (!string.IsNullOrEmpty(deploymentResult.ErrorText))
                        OutputTextBlock.Text += $"[ERROR] {deploymentResult.ErrorText}\n";
                    
                    if (deploymentResult.ActivityId != Guid.Empty)
                        OutputTextBlock.Text += $"[INFO] Activity ID: {deploymentResult.ActivityId}\n";
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Exception: {ex.Message}\n";
                OutputTextBlock.Text += $"[DEBUG] HRESULT: 0x{ex.HResult:X8}\n";
                OutputTextBlock.Text += $"[DEBUG] Type: {ex.GetType().Name}\n";
                
                if (ex.InnerException != null)
                    OutputTextBlock.Text += $"[DEBUG] Inner: {ex.InnerException.Message}\n";
                
                // Provide debugging suggestions
                OutputTextBlock.Text += $"\n[INFO] Troubleshooting:\n";
                OutputTextBlock.Text += $"- Verify AppxManifest.xml is valid XML\n";
                OutputTextBlock.Text += $"- Check that Identity Name, Publisher, and Version are correct\n";
                OutputTextBlock.Text += $"- Ensure all referenced assets exist (images folder, etc.)\n";
                OutputTextBlock.Text += $"- Verify the app has restricted 'packageManagement' capability declared\n";
                
                return false;
            }
        }

        private void UpdateStatus(string status)
        {
            StatusTextBlock.Text = status;
            OutputTextBlock.Text += $"\n[STATUS] {status}\n";
        }
    }
}
