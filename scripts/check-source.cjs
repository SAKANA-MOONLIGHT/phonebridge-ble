const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
// A generic OpenStep plist reader validates the checked-in Xcode project,
// independently of the generator's string construction.
function parsePlist(input) {
  const tokens = input.match(/"(?:\\.|[^"\\])*"|\/\/[^\r\n]*|[{}()=;,]|[^\s{}()=;,]+/g).filter(x => !x.startsWith('//'));
  let i = 0;
  const expect = value => { assert.equal(tokens[i++], value, `Expected ${value} near token ${i}`); };
  const scalar = () => { const t = tokens[i++]; assert(t && !'{}()=;,'.includes(t), `Invalid scalar ${t}`); return t.startsWith('"') ? JSON.parse(t) : t; };
  function value() {
    if (tokens[i] === '{') {
      i++; const o = {};
      while (tokens[i] !== '}') { const key = scalar(); expect('='); o[key] = value(); expect(';'); }
      i++; return o;
    }
    if (tokens[i] === '(') {
      i++; const a = [];
      while (tokens[i] !== ')') { a.push(value()); if (tokens[i] !== ')') expect(','); }
      i++; return a;
    }
    return scalar();
  }
  const result = value(); assert.equal(i, tokens.length); return result;
}
const project = parsePlist(fs.readFileSync(path.join(root, 'ios/PhoneBridge.xcodeproj/project.pbxproj'), 'utf8'));
const objects = project.objects;
assert.equal(objects[project.rootObject].isa, 'PBXProject');
const targets = objects[project.rootObject].targets.map(id => objects[id]);
assert.equal(targets.length, 2);
assert(targets.some(target => target.productType === 'com.apple.product-type.app-extension'));
for (const object of Object.values(objects)) {
  if (object.isa === 'PBXFileReference' && object.sourceTree === 'SOURCE_ROOT') assert(fs.existsSync(path.join(root, 'ios', object.path)), `Missing source: ${object.path}`);
  if (object.fileRef) assert(objects[object.fileRef], `Dangling fileRef ${object.fileRef}`);
  if (object.buildPhases) for (const id of object.buildPhases) assert(objects[id], `Dangling build phase ${id}`);
}
const csharp = fs.readFileSync(path.join(root, 'windows/Core.cs'), 'utf8');
const swift = fs.readFileSync(path.join(root, 'ios/Broadcast/FrameWire.swift'), 'utf8');
const csGuids = [...csharp.matchAll(/new Guid\("([a-f0-9-]{36})"\)/g)].map(x=>x[1].toLowerCase()).sort();
const swiftGuids = [...swift.matchAll(/= "([A-F0-9-]{36})"/g)].map(x=>x[1].toLowerCase()).sort();
assert.deepEqual(csGuids, swiftGuids);
assert.equal(csGuids.length, 3);
const manifest = fs.readFileSync(path.join(root,'windows/app.manifest'),'utf8');
assert(manifest.includes('level="asInvoker"'));
const swiftHost = fs.readFileSync(path.join(root,'ios/PhoneBridge/PhoneBridgeApp.swift'),'utf8');
assert(swiftHost.includes('RPSystemBroadcastPickerView'));
assert(swiftHost.includes('builtInPlugInsURL'));
assert(!swiftHost.includes('RPScreenRecorder.shared().startCapture'));
const extension = fs.readFileSync(path.join(root,'ios/Broadcast/Info.plist'),'utf8');
assert(extension.includes('com.apple.broadcast-services-upload'));
assert(extension.includes('NSBluetoothAlwaysUsageDescription'));
console.log('PASS: Xcode project structure, source references, shared BLE UUIDs, ordinary-user manifest and broadcast extension configuration.');
console.log('Static checks do not prove Swift compilation, signing, installation, or real iPhone screen sharing.');
