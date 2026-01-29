using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Store_Acquisition_Assistant
{
    public sealed partial class MainPage : Page
    {
        private PackageManager packageManager = new PackageManager();

        public MainPage()
        {
            this.InitializeComponent();
            this.GoButton.Click += GoButton_Click;
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
                string identityName = await FetchProductIdentityAsync(productId);

                if (string.IsNullOrEmpty(identityName))
                {
                    UpdateStatus("Error: Could not extract Identity Name from product data");
                    return;
                }

                IdentityTextBlock.Text = identityName;
                OutputTextBlock.Text += $"[✓] Identity Name fetched: {identityName}\n\n";

                // 2. Ask user for a staging location
                UpdateStatus("Please select a folder to stage the app files...");
                OutputTextBlock.Text += "[INFO] Opening folder picker...\n";
                
                FolderPicker picker = new FolderPicker();
                picker.SuggestedStartLocation = PickerLocationId.Desktop;
                picker.FileTypeFilter.Add("*");
                picker.CommitButtonText = "Select Staging Folder";
                
                StorageFolder stagingFolder = await picker.PickSingleFolderAsync();
                
                if (stagingFolder == null)
                {
                    UpdateStatus("Operation cancelled by user.");
                    return;
                }

                OutputTextBlock.Text += $"[INFO] Staging folder selected: {stagingFolder.Path}\n";

                // 3. Copy template files to staging folder
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

                bool manifestUpdated = await UpdateAppxManifestAsync(stagingFolder, identityName);
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

                // 6. Copy to LocalFolder for registration
                UpdateStatus("Preparing deployment...");
                OutputTextBlock.Text += "[INFO] Copying to app's local storage...\n";
                
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                StorageFolder deployFolder = await localFolder.CreateFolderAsync("deploy", CreationCollisionOption.ReplaceExisting);
                
                // Copy all files from staging to deploy folder
                await CopyFolderAsync(stagingFolder, deployFolder);
                OutputTextBlock.Text += "[✓] Files copied to local storage\n";

                // 6. Deploy
                UpdateStatus("Deploying app...");
                bool deployed = await DeployAppPackageAsync(deployFolder, identityName);
                
                if (deployed)
                {
                    UpdateStatus("App deployed successfully!");
                    OutputTextBlock.Text += "\n[✓] Process complete!";
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

        private async Task<string> FetchProductIdentityAsync(string productId)
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
                    
                    // Check LocalizedProperties first
                    if (productObject.TryGetValue("LocalizedProperties", out IJsonValue locVal) && locVal.ValueType == JsonValueType.Array)
                    {
                        var arr = locVal.GetArray();
                        if (arr.Count > 0)
                        {
                            var prop = arr.GetObjectAt(0);
                            if (prop.TryGetValue("PackageIdentityName", out IJsonValue pid) && pid.ValueType == JsonValueType.String)
                                return pid.GetString();
                        }
                    }

                    // Check Properties
                    if (productObject.TryGetValue("Properties", out IJsonValue propsVal) && propsVal.ValueType == JsonValueType.Object)
                    {
                        var props = propsVal.GetObject();
                        if (props.TryGetValue("PackageIdentityName", out IJsonValue pid) && pid.ValueType == JsonValueType.String)
                            return pid.GetString();
                    }
                }
                return null;
            }
        }

        private async Task<bool> UpdateAppxManifestAsync(StorageFolder workingFolder, string identityName)
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

                // Update Identity Name ONLY
                var identityElement = doc.Descendants(ns + "Identity").FirstOrDefault();
                if (identityElement != null)
                {
                    identityElement.SetAttributeValue("Name", identityName);
                    // We leave Publisher and other attributes exactly as they are in your template
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

                // Try RegisterPackageAsync with manifest file URI (what PowerShell uses)
                Uri manifestUri = new Uri(manifestPath);
                OutputTextBlock.Text += $"[INFO] Manifest URI: {manifestUri}\n";
                OutputTextBlock.Text += $"[INFO] Attempting RegisterPackageAsync...\n";

                var deploymentResult = await packageManager.RegisterPackageAsync(
                    manifestUri,
                    null,
                    DeploymentOptions.None);

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