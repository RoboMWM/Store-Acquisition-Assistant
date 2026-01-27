using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Management.Deployment;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Store_Acquisition_Assistant
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
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

                // Fetch the product information from Microsoft Store catalog
                string identityName = await FetchProductIdentityAsync(productId);

                if (string.IsNullOrEmpty(identityName))
                {
                    UpdateStatus("Error: Could not extract Identity Name from product data");
                    return;
                }

                IdentityTextBlock.Text = identityName;
                OutputTextBlock.Text += $"[✓] Identity Name fetched: {identityName}\n\n";

                UpdateStatus("Updating configuration files...");

                // Update AppxManifest.xml with new Identity Name
                bool manifestUpdated = await UpdateAppxManifestAsync(identityName);
                if (manifestUpdated)
                {
                    OutputTextBlock.Text += "[✓] Updated newapp/AppxManifest.xml with new Identity\n";
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: Could not update AppxManifest.xml\n";
                }

                // Update main.js with Product ID
                bool jsUpdated = await UpdateMainJsAsync(productId);
                if (jsUpdated)
                {
                    OutputTextBlock.Text += $"[✓] Updated newapp/main.js with Product ID: {productId}\n";
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: Could not update main.js\n";
                }

                UpdateStatus("Deploying app...");

                // Deploy the app package
                // FIXED: Passing identityName so we can look up the FamilyName later
                bool deployed = await DeployAppPackageAsync(identityName);
                if (deployed)
                {
                    OutputTextBlock.Text += "[✓] App package deployed successfully!\n";
                    UpdateStatus("App deployed successfully!");
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: App deployment completed with some messages\n";
                    UpdateStatus("App deployment completed");
                }

                OutputTextBlock.Text += "\n[✓] Process complete!";
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
                OutputTextBlock.Text += $"\nException: {ex.Message}\n{ex.StackTrace}";
            }
        }

        private async Task<string> FetchProductIdentityAsync(string productId)
        {
            string url = $"https://displaycatalog.mp.microsoft.com/v7.0/products/{productId}/0010?fieldsTemplate=InstallAgent&market=US&languages=en-US,en,impartial";

            using (HttpClient client = new HttpClient())
            {
                try
                {
                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    string content = await response.Content.ReadAsStringAsync();
                    OutputTextBlock.Text += $"[✓] HTTP 200 OK - Response received\n";
                    OutputTextBlock.Text += $"Response length: {content.Length} bytes\n";

                    // Trim BOM if present
                    content = content.TrimStart('\uFEFF');

                    try
                    {
                        // Parse as JSON using Windows.Data.Json
                        JsonObject jsonObject = JsonObject.Parse(content);
                        OutputTextBlock.Text += $"[✓] Successfully parsed JSON response\n";

                        IJsonValue productValue;
                        if (jsonObject.TryGetValue("Product", out productValue) && productValue.ValueType == JsonValueType.Object)
                        {
                            JsonObject productObject = productValue.GetObject();
                            OutputTextBlock.Text += "[INFO] Found 'Product' object\n";

                            // Check LocalizedProperties array for PackageIdentityName
                            IJsonValue localizedPropsValue;
                            if (productObject.TryGetValue("LocalizedProperties", out localizedPropsValue) && localizedPropsValue.ValueType == JsonValueType.Array)
                            {
                                JsonArray localizedPropsArray = localizedPropsValue.GetArray();
                                OutputTextBlock.Text += $"[INFO] Found LocalizedProperties array with {localizedPropsArray.Count} items\n";

                                if (localizedPropsArray.Count > 0)
                                {
                                    IJsonValue firstPropValue = localizedPropsArray.GetObjectAt(0);
                                    if (firstPropValue.ValueType == JsonValueType.Object)
                                    {
                                        JsonObject firstProp = firstPropValue.GetObject();

                                        // Look for PackageIdentityName in first localized property
                                        IJsonValue identityValue;
                                        if (firstProp.TryGetValue("PackageIdentityName", out identityValue) && identityValue.ValueType == JsonValueType.String)
                                        {
                                            string identityName = identityValue.GetString();
                                            if (!string.IsNullOrEmpty(identityName))
                                            {
                                                OutputTextBlock.Text += $"[✓] Found PackageIdentityName: {identityName}\n";
                                                return identityName;
                                            }
                                        }

                                        // List properties in first LocalizedProperty for debugging
                                        OutputTextBlock.Text += "[INFO] Properties in LocalizedProperties[0]:\n";
                                        foreach (var prop in firstProp)
                                        {
                                            OutputTextBlock.Text += $"  - {prop.Key}\n";
                                        }
                                    }
                                }
                            }

                            // Check Properties object
                            IJsonValue propertiesValue;
                            if (productObject.TryGetValue("Properties", out propertiesValue) && propertiesValue.ValueType == JsonValueType.Object)
                            {
                                JsonObject propertiesObject = propertiesValue.GetObject();
                                OutputTextBlock.Text += "[INFO] Found 'Properties' object\n";

                                IJsonValue identityValue;
                                if (propertiesObject.TryGetValue("PackageIdentityName", out identityValue) && identityValue.ValueType == JsonValueType.String)
                                {
                                    string identityName = identityValue.GetString();
                                    if (!string.IsNullOrEmpty(identityName))
                                    {
                                        OutputTextBlock.Text += $"[✓] Found PackageIdentityName in Properties: {identityName}\n";
                                        return identityName;
                                    }
                                }

                                // List available properties
                                OutputTextBlock.Text += "[INFO] Available properties:\n";
                                foreach (var prop in propertiesObject)
                                {
                                    OutputTextBlock.Text += $"  - {prop.Key}\n";
                                }
                            }

                            OutputTextBlock.Text += "[!] Could not find PackageIdentityName in standard locations.\n";
                        }
                        else
                        {
                            OutputTextBlock.Text += "[!] 'Product' property not found in JSON\n";
                        }

                        return null;
                    }
                    catch (Exception parseEx)
                    {
                        OutputTextBlock.Text += $"[✗] JSON Parse Error: {parseEx.Message}\n";
                        OutputTextBlock.Text += $"Stack Trace: {parseEx.StackTrace}\n";
                        throw;
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    OutputTextBlock.Text += $"[✗] HTTP Error: {httpEx.Message}\n";
                    throw;
                }
            }
        }

        private async Task<bool> UpdateAppxManifestAsync(string identityName)
        {
            try
            {
                // Access files from the app's installation directory
                StorageFolder installedFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
                StorageFolder newappFolder = await installedFolder.GetFolderAsync("newapp");
                StorageFile manifestFile = await newappFolder.GetFileAsync("AppxManifest.template.xml");

                if (manifestFile == null)
                {
                    OutputTextBlock.Text += "[!] AppxManifest.template.xml not found in newapp folder\n";
                    return false;
                }

                string content = await FileIO.ReadTextAsync(manifestFile);
                XDocument doc = XDocument.Parse(content);

                // Find and update the Identity element
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identityElement = doc.Descendants(ns + "Identity").FirstOrDefault();

                if (identityElement != null)
                {
                    identityElement.SetAttributeValue("Name", identityName);

                    // Save to LocalFolder for access and deployment
                    StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                    StorageFile deploymentManifest = await localFolder.CreateFileAsync("AppxManifest.xml", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(deploymentManifest, doc.ToString());

                    OutputTextBlock.Text += "[✓] Generated AppxManifest.xml for deployment\n";
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error updating AppxManifest: {ex.Message}\n";
                return false;
            }
        }

        private async Task<bool> UpdateMainJsAsync(string productId)
        {
            try
            {
                // Access files from the app's installation directory
                StorageFolder installedFolder = Windows.ApplicationModel.Package.Current.InstalledLocation;
                StorageFolder newappFolder = await installedFolder.GetFolderAsync("newapp");
                StorageFile jsFile = await newappFolder.GetFileAsync("main.js");

                if (jsFile == null)
                {
                    OutputTextBlock.Text += "[!] main.js not found in newapp folder\n";
                    return false;
                }

                string content = await FileIO.ReadTextAsync(jsFile);

                // Replace ONESTOREID with the product ID
                string updatedContent = content.Replace("ONESTOREID", productId);

                // Save to LocalFolder
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                StorageFile deploymentJs = await localFolder.CreateFileAsync("main.js", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(deploymentJs, updatedContent);

                return true;
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error updating main.js: {ex.Message}\n";
                return false;
            }
        }

        // FIXED: Added identityName parameter to look up package family later
        private async Task<bool> DeployAppPackageAsync(string identityName)
        {
            try
            {
                // Get the updated configuration files from LocalFolder
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;

                // Check if manifest and main.js were updated
                StorageFile manifestFile = await localFolder.GetFileAsync("AppxManifest.xml");
                StorageFile jsFile = await localFolder.GetFileAsync("main.js");

                if (manifestFile == null || jsFile == null)
                {
                    OutputTextBlock.Text += "[!] Configuration files not found in LocalFolder\n";
                    return false;
                }

                OutputTextBlock.Text += "[✓] Configuration files prepared\n";
                OutputTextBlock.Text += $"[INFO] Manifest: {manifestFile.Path}\n";
                OutputTextBlock.Text += $"[INFO] JS file: {jsFile.Path}\n\n";

                // Now attempt deployment with packageManagement capability
                OutputTextBlock.Text += "[INFO] Attempting package deployment...\n";

                PackageManager pm = new PackageManager();

                try
                {
                    // Deploy the package from LocalFolder which contains the updated AppxManifest.xml
                    var deploymentResult = await pm.AddPackageAsync(
                        new Uri(localFolder.Path),
                        null,
                        DeploymentOptions.ForceApplicationShutdown);

                    if (deploymentResult.IsRegistered)
                    {
                        OutputTextBlock.Text += "[✓] Package deployed successfully!\n";

                        // FIXED: Look up the package manually since DeploymentResult doesn't have PackageFamilyName
                        try
                        {
                            var pkg = pm.FindPackagesForUser(string.Empty)
                                        .FirstOrDefault(p => p.Id.Name.Equals(identityName, StringComparison.OrdinalIgnoreCase));

                            if (pkg != null)
                            {
                                OutputTextBlock.Text += $"[INFO] Package Family: {pkg.Id.FamilyName}\n";
                            }
                        }
                        catch
                        {
                            OutputTextBlock.Text += "[INFO] Could not retrieve Package Family Name immediately.\n";
                        }

                        return true;
                    }
                    else
                    {
                        OutputTextBlock.Text += $"[!] Deployment failed - Not registered\n";
                        if (!string.IsNullOrEmpty(deploymentResult.ErrorText))
                        {
                            OutputTextBlock.Text += $"[ERROR] {deploymentResult.ErrorText}\n";
                        }
                        return false;
                    }
                }
                catch (Exception deployEx)
                {
                    OutputTextBlock.Text += $"[!] Deployment exception: {deployEx.Message}\n";
                    OutputTextBlock.Text += $"[DEBUG] HRESULT: 0x{deployEx.HResult:X8}\n";

                    // Provide guidance
                    OutputTextBlock.Text += "\n[INFO] If deployment failed due to permissions:\n";
                    OutputTextBlock.Text += "- The app may need to be signed as a system app\n";
                    OutputTextBlock.Text += "- Or use 'Add-AppxPackage -Register' in PowerShell as admin\n";
                    OutputTextBlock.Text += $"- Command: Add-AppxPackage -Register '{manifestFile.Path}'\n";

                    return false;
                }
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error in deployment: {ex.Message}\n";
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