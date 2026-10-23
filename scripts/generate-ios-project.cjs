// No npm packages or XcodeGen required. Deterministic, conventional Xcode project.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..', 'ios');
const bundle = process.argv[2] || 'local.phonebridge.app';
if (!/^[A-Za-z][A-Za-z0-9-]*(\.[A-Za-z0-9-]+){2,}$/.test(bundle)) throw new Error('Use a reverse-DNS bundle identifier such as com.yourname.phonebridge');
let next = 1;
const objects = new Map();
const id = () => (next++).toString(16).toUpperCase().padStart(24, '0');
const project = id(), main = id(), products = id(), host = id(), ext = id();
const appProduct = id(), extProduct = id();
const q = value => JSON.stringify(String(value));
const ref = value => value;
const list = values => values.length ? '(' + values.join(', ') + ',)' : '()';
const add = (key, type, body) => objects.set(key, `{ isa = ${type}; ${body} }`);
const file = (name, type) => { const key = id(); add(key, 'PBXFileReference', `lastKnownFileType = ${type}; path = ${q(name)}; sourceTree = SOURCE_ROOT;`); return key; };
const build = (fileRef, attributes='') => { const key = id(); add(key, 'PBXBuildFile', `fileRef = ${ref(fileRef)}; ${attributes}`); return key; };
const phase = (type, files, more='') => { const key = id(); add(key, type, `buildActionMask = 2147483647; files = ${list(files)}; runOnlyForDeploymentPostprocessing = 0; ${more}`); return key; };
const configList = settings => {
  const configs = ['Debug', 'Release'].map(name => {
    const key = id(); const merged = {...settings, SWIFT_OPTIMIZATION_LEVEL: name === 'Debug' ? '-Onone' : '-O'};
    if (name === 'Debug') merged.SWIFT_ACTIVE_COMPILATION_CONDITIONS = 'DEBUG';
    add(key, 'XCBuildConfiguration', `name = ${name}; buildSettings = { ${Object.entries(merged).map(([k,v]) => `${k} = ${q(v)};`).join(' ')} };`); return key;
  });
  const key = id(); add(key, 'XCConfigurationList', `buildConfigurations = ${list(configs)}; defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;`); return key;
};
const hostSource = file('PhoneBridge/PhoneBridgeApp.swift', 'sourcecode.swift');
const extensionSources = ['FrameWire', 'CaptureGate', 'BluetoothTransport', 'SampleHandler'].map(name => file(`Broadcast/${name}.swift`, 'sourcecode.swift'));
const hostInfo = file('PhoneBridge/Info.plist', 'text.plist.xml'), extInfo = file('Broadcast/Info.plist', 'text.plist.xml');
add(appProduct, 'PBXFileReference', 'explicitFileType = wrapper.application; path = PhoneBridge.app; sourceTree = BUILT_PRODUCTS_DIR;');
add(extProduct, 'PBXFileReference', 'explicitFileType = "wrapper.app-extension"; path = PhoneBridgeBroadcast.appex; sourceTree = BUILT_PRODUCTS_DIR;');
const hostSources = phase('PBXSourcesBuildPhase', [build(hostSource)]);
const extSources = phase('PBXSourcesBuildPhase', extensionSources.map(source => build(source)));
const embed = phase('PBXCopyFilesBuildPhase', [build(extProduct, 'settings = { ATTRIBUTES = (RemoveHeadersOnCopy,); };')], 'dstPath = ""; dstSubfolderSpec = 13; name = "Embed App Extensions";');
const proxy = id(), dep = id();
add(proxy, 'PBXContainerItemProxy', `containerPortal = ${project}; proxyType = 1; remoteGlobalIDString = ${ext}; remoteInfo = PhoneBridgeBroadcast;`);
add(dep, 'PBXTargetDependency', `target = ${ext}; targetProxy = ${proxy};`);
const base = {SDKROOT: 'iphoneos', IPHONEOS_DEPLOYMENT_TARGET: '17.0', SWIFT_VERSION: '5.0', CLANG_ENABLE_MODULES: 'YES', ENABLE_USER_SCRIPT_SANDBOXING: 'YES'};
const projectConfigs = configList(base);
const targetBase = {CODE_SIGN_STYLE: 'Automatic', TARGETED_DEVICE_FAMILY: '1,2', GENERATE_INFOPLIST_FILE: 'NO', PRODUCT_NAME: '$(TARGET_NAME)', LD_RUNPATH_SEARCH_PATHS: '$(inherited) @executable_path/Frameworks'};
const hostConfigs = configList({...targetBase, PRODUCT_BUNDLE_IDENTIFIER: bundle, INFOPLIST_FILE: 'PhoneBridge/Info.plist'});
const extConfigs = configList({...targetBase, PRODUCT_BUNDLE_IDENTIFIER: bundle + '.Broadcast', INFOPLIST_FILE: 'Broadcast/Info.plist', APPLICATION_EXTENSION_API_ONLY: 'YES', SKIP_INSTALL: 'YES', LD_RUNPATH_SEARCH_PATHS: '$(inherited) @executable_path/Frameworks @executable_path/../../Frameworks'});
add(host, 'PBXNativeTarget', `buildConfigurationList = ${hostConfigs}; buildPhases = ${list([hostSources, phase('PBXFrameworksBuildPhase', []), phase('PBXResourcesBuildPhase', []), embed])}; buildRules = (); dependencies = ${list([dep])}; name = PhoneBridge; productName = PhoneBridge; productReference = ${appProduct}; productType = "com.apple.product-type.application";`);
add(ext, 'PBXNativeTarget', `buildConfigurationList = ${extConfigs}; buildPhases = ${list([extSources, phase('PBXFrameworksBuildPhase', []), phase('PBXResourcesBuildPhase', [])])}; buildRules = (); dependencies = (); name = PhoneBridgeBroadcast; productName = PhoneBridgeBroadcast; productReference = ${extProduct}; productType = "com.apple.product-type.app-extension";`);
add(products, 'PBXGroup', `children = ${list([appProduct,extProduct])}; name = Products; sourceTree = "<group>";`);
add(main, 'PBXGroup', `children = ${list([hostSource,hostInfo,...extensionSources,extInfo,products])}; sourceTree = "<group>";`);
add(project, 'PBXProject', `attributes = { LastUpgradeCheck = 1630; TargetAttributes = { ${host} = { CreatedOnToolsVersion = 16.3; }; ${ext} = { CreatedOnToolsVersion = 16.3; }; }; }; buildConfigurationList = ${projectConfigs}; compatibilityVersion = "Xcode 14.0"; developmentRegion = en; hasScannedForEncodings = 0; knownRegions = (en, Base, "zh-Hans",); mainGroup = ${main}; productRefGroup = ${products}; projectDirPath = ""; projectRoot = ""; targets = ${list([host,ext])};`);
const projectDir = path.join(root, 'PhoneBridge.xcodeproj');
fs.mkdirSync(path.join(projectDir, 'xcshareddata', 'xcschemes'), {recursive:true});
const text = '// !$*UTF8*$!\n{\narchiveVersion = 1;\nclasses = {};\nobjectVersion = 56;\nobjects = {\n' + [...objects].map(([key,value]) => `${key} = ${value};`).join('\n') + `\n};\nrootObject = ${project};\n}\n`;
fs.writeFileSync(path.join(projectDir, 'project.pbxproj'), text);
fs.writeFileSync(path.join(projectDir, 'xcshareddata', 'xcschemes', 'PhoneBridge.xcscheme'), `<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="1630" version="1.3">
 <BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="${host}" BuildableName="PhoneBridge.app" BlueprintName="PhoneBridge" ReferencedContainer="container:PhoneBridge.xcodeproj"/></BuildActionEntry></BuildActionEntries></BuildAction>
 <LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0"><BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="${host}" BuildableName="PhoneBridge.app" BlueprintName="PhoneBridge" ReferencedContainer="container:PhoneBridge.xcodeproj"/></BuildableProductRunnable></LaunchAction>
 <ProfileAction buildConfiguration="Release" shouldUseLaunchSchemeArgsEnv="YES" savedToolIdentifier="" useCustomWorkingDirectory="NO" debugDocumentVersioning="YES"/>
 <AnalyzeAction buildConfiguration="Debug"/>
 <ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/>
</Scheme>\n`);
console.log(`Generated ${projectDir} (${bundle})`);
