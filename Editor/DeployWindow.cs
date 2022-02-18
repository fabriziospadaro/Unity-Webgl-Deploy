using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using System;
using Renci.SshNet;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

struct DeploySettings {
  public string ipServer;
  public string appName;
  public string deployUsername;
  public string deployRootPath;
  public string deployFolder;
  public string domain;
  public string location;
  public string privateKeyPath;
  public string buildFolderName;
  public string buildSceneName;
  public string cloudFlareZone;
  public string cloudFlareEmail;
  public string cloudFlareKey;
}
public class DeployWindow : EditorWindow {
  private DeploySettings deploySettings;

  [MenuItem("Window/Deploy")]
  static void Initialize() {
    DeployWindow window = (DeployWindow)GetWindow(typeof(DeployWindow));
    window.Show();
  }

  public void OnEnable() {
    LoadSettings();
  }

  void LoadSettings() {
    string settingsPath = File.ReadAllText(Path.GetFullPath("Packages/com.spadaro.webgl-deploy/Configs/deployCfg.conf"));
    try {
      deploySettings = (DeploySettings)JsonUtility.FromJson(File.ReadAllText(settingsPath), typeof(DeploySettings));
    }
    catch {
      deploySettings = new DeploySettings();
    }
  }

  void SaveSettings(object obj) {
    string settingsPath = File.ReadAllText(Path.GetFullPath("Packages/com.spadaro.webgl-deploy/Configs/deployCfg.conf"));
    string settingsRaw = JsonUtility.ToJson(obj);
    File.WriteAllText(settingsPath, settingsRaw);
    AssetDatabase.Refresh();
  }

  void OnGUI() {
    EditorGUI.BeginChangeCheck();

    ServerInspector();
    NgnixInspector();
    CloudFlareInspector();
    BuildInspector();
    if(EditorGUI.EndChangeCheck())
      SaveSettings(deploySettings);

    ActionsInspector();
  }

  void ServerInspector() {
    GUILayout.BeginVertical("", EditorStyles.helpBox);
    GUILayout.Label("Server settings", EditorStyles.boldLabel);
    EditorGUI.indentLevel++;
    GUILayout.Space(5);
    deploySettings.ipServer = EditorGUILayout.TextField("Ip server:", deploySettings.ipServer);
    deploySettings.deployUsername = EditorGUILayout.TextField("Deploy username:", deploySettings.deployUsername);
    deploySettings.deployRootPath = EditorGUILayout.TextField("Deploy root path:", deploySettings.deployRootPath);
    deploySettings.deployFolder = EditorGUILayout.TextField("Deploy folder:", deploySettings.deployFolder);
    deploySettings.privateKeyPath = EditorGUILayout.TextField("PrivateKey path:", deploySettings.privateKeyPath);
    EditorGUI.indentLevel--;
    GUILayout.EndVertical();
  }

  void NgnixInspector() {
    GUILayout.Space(5);
    GUILayout.BeginVertical("", EditorStyles.helpBox);
    GUILayout.Label("Ngnix settings", EditorStyles.boldLabel);
    EditorGUI.indentLevel++;
    deploySettings.domain = EditorGUILayout.TextField("Domain name:", deploySettings.domain);
    deploySettings.location = EditorGUILayout.TextField("Location:", deploySettings.location);
    EditorGUI.indentLevel--;
    GUILayout.EndVertical();
  }

  void BuildInspector() {
    GUILayout.Space(5);
    GUILayout.BeginVertical("", EditorStyles.helpBox);
    GUILayout.Label("Build settings", EditorStyles.boldLabel);
    EditorGUI.indentLevel++;
    deploySettings.appName = EditorGUILayout.TextField("App name:", deploySettings.appName);
    deploySettings.buildFolderName = EditorGUILayout.TextField("Build folder name:", deploySettings.buildFolderName);
    SceneInspector();
    EditorGUI.indentLevel--;
    GUILayout.EndVertical();
  }

  void CloudFlareInspector() {
    GUILayout.Space(5);
    GUILayout.BeginVertical("", EditorStyles.helpBox);
    GUILayout.Label("CloudFlare settings", EditorStyles.boldLabel);
    EditorGUI.indentLevel++;
    deploySettings.cloudFlareEmail = EditorGUILayout.TextField("Email:", deploySettings.cloudFlareEmail);
    deploySettings.cloudFlareKey = EditorGUILayout.TextField("Key:", deploySettings.cloudFlareKey);
    deploySettings.cloudFlareZone = EditorGUILayout.TextField("Zone:", deploySettings.cloudFlareZone);
    EditorGUI.indentLevel--;
    GUILayout.EndVertical();
  }

  void ActionsInspector() {
    GUILayout.Space(10);
    GUILayout.BeginHorizontal();

    if(GUILayout.Button("Build & Deploy")) {
      Build(() => {
        EditorUtility.DisplayProgressBar("Deploy 1/3", "Deploying build on the server...", 0.5f);
        Deploy();
        EditorUtility.DisplayProgressBar("Deploy 2/3", "Updating ngnix conf...", 1f);
        CreateNgnixConf();
        EditorUtility.DisplayProgressBar("Deploy 3/3", "Purging data cache...", 1f);
        PurgeCache();
      });
    }

    if(GUILayout.Button("Deploy")) {
      EditorUtility.DisplayProgressBar("Deploy 1/3", "Deploying build on the server...", 0.5f);
      Deploy();
      EditorUtility.DisplayProgressBar("Deploy 2/3", "Updating ngnix conf...", 1);
      CreateNgnixConf();
      EditorUtility.DisplayProgressBar("Deploy 3/3", "Purging data cache...", 1f);
      PurgeCache();
    }

    if(GUILayout.Button("Update Ngnix")) {
      EditorUtility.DisplayProgressBar("Deploy 1/1", "Updating ngnix conf...", 1);
      CreateNgnixConf();
    }
    if(GUILayout.Button("Purge cache")) {
      EditorUtility.DisplayProgressBar("Ngnix", "Purging cache...", 1);
      CreateNgnixConf();
    }
    EditorUtility.ClearProgressBar();
    GUILayout.EndHorizontal();
  }

  void SceneInspector() {
    var oldScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(deploySettings.buildSceneName);

    EditorGUI.BeginChangeCheck();
    var newScene = EditorGUILayout.ObjectField("Scene", oldScene, typeof(SceneAsset), false) as SceneAsset;

    if(EditorGUI.EndChangeCheck()) {
      var newPath = AssetDatabase.GetAssetPath(newScene);
      deploySettings.buildSceneName = newPath;
    }
  }

  //https://gist.github.com/piccaso/d963331dcbf20611b094
  public void Deploy() {
    string remotePath = $"/{deploySettings.deployRootPath}/{deploySettings.deployUsername}/{deploySettings.deployFolder}/{deploySettings.appName}";
    string localPath = Directory.GetCurrentDirectory() + "/" + deploySettings.buildFolderName;

    using(var sftp = new SftpClient(ConnInfoFor(deploySettings.deployUsername))) {
      sftp.Connect();

      if(sftp.Exists(remotePath))
        DeleteDirectory(sftp, remotePath);
      sftp.CreateDirectory(remotePath);

      UploadDirectory(sftp, localPath, remotePath);
      sftp.Disconnect();
    }
  }


  void UploadDirectory(SftpClient client, string localPath, string remotePath) {
    IEnumerable<FileSystemInfo> infos = new DirectoryInfo(localPath).EnumerateFileSystemInfos();
    foreach(FileSystemInfo info in infos) {
      if(info.Attributes.HasFlag(FileAttributes.Directory)) {
        string subPath = remotePath + "/" + info.Name;
        if(!client.Exists(subPath))
          client.CreateDirectory(subPath);

        UploadDirectory(client, info.FullName, remotePath + "/" + info.Name);
      }
      else {
        using(var fileStream = new FileStream(info.FullName, FileMode.Open))
          client.UploadFile(fileStream, remotePath + "/" + info.Name);
      }
    }
  }

  private static void DeleteDirectory(SftpClient client, string path) {
    foreach(Renci.SshNet.Sftp.SftpFile file in client.ListDirectory(path)) {
      if((file.Name != ".") && (file.Name != "..")) {
        if(file.IsDirectory) {
          DeleteDirectory(client, file.FullName);
        }
        else {
          client.DeleteFile(file.FullName);
        }
      }
    }

    client.DeleteDirectory(path);
  }

  void PurgeCache() {
    using(var httpClient = new HttpClient()) {
      using(var request = new HttpRequestMessage(new HttpMethod("POST"),$"https://api.cloudflare.com/client/v4/zones/{deploySettings.cloudFlareZone}/purge_cache")) {
        request.Headers.TryAddWithoutValidation("X-Auth-Email", deploySettings.cloudFlareEmail);
        request.Headers.TryAddWithoutValidation("X-Auth-Key", deploySettings.cloudFlareKey);

        request.Content = new StringContent("{\"files\":[\""+ fullUrl +"\"]}");
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        
        httpClient.SendAsync(request);
      }
    }
  }

  string fullUrl{
    get {
      return deploySettings.location == ""
        ? $"https://{deploySettings.domain}/Build/Build.data.gz"
        : $"https://{deploySettings.domain}/{deploySettings.location}/Build/Build.data.gz";
    }
  }

  void CreateNgnixConf() {
    string rawConf = File.ReadAllText(Path.GetFullPath("Packages/com.spadaro.webgl-deploy/Configs/ngnix.conf"));

    rawConf = rawConf.Replace("*SERVER_NAME", deploySettings.domain + " " + "www." + deploySettings.domain);
    rawConf = rawConf.Replace("*LOCATION", "/" + deploySettings.location);
    rawConf = rawConf.Replace("*BUILD_PATH", $"/{deploySettings.deployRootPath}/{deploySettings.deployUsername}/{deploySettings.deployFolder}/{deploySettings.appName}");
    rawConf = rawConf.Replace("*ALIAS_ROOT", deploySettings.location == "" ? "root" : "alias");
    using(var sshclient = new SshClient(ConnInfoFor("root"))) {
      sshclient.Connect();
      string echo_NgnixConf = $"echo \"{rawConf}\" > /etc/nginx/sites-available/{deploySettings.appName}";
      string ln = $"ln -s /etc/nginx/sites-available/{deploySettings.appName} /etc/nginx/sites-enabled/{deploySettings.appName}";
      string restartNginx = "/etc/init.d/nginx reload";
      using(var cmd = sshclient.CreateCommand($"{echo_NgnixConf} ;{ln}; sleep 1 && {restartNginx}"))
        EditorUtility.DisplayDialog("Build status", cmd.Execute(), "ok");
      sshclient.Disconnect();
    }
  }

  public void Build(Action onSuccess) {
    BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions();
    buildPlayerOptions.scenes = new[] { deploySettings.buildSceneName };
    buildPlayerOptions.locationPathName = deploySettings.buildFolderName;
    buildPlayerOptions.target = BuildTarget.WebGL;
    buildPlayerOptions.options = BuildOptions.None;

    BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
    BuildSummary summary = report.summary;

    if(summary.result == BuildResult.Succeeded) {
      onSuccess();
    }

    if(summary.result == BuildResult.Failed) {
      Debug.Log("Build failed " + summary.ToString());
    }
  }

  public ConnectionInfo ConnInfoFor(string user){
    return new ConnectionInfo(deploySettings.ipServer, 22, user,
      new AuthenticationMethod[]{
        new PrivateKeyAuthenticationMethod(user,new PrivateKeyFile[]{
          new PrivateKeyFile(deploySettings.privateKeyPath ,"passphrase")
        }),
      }
    );
  }
}