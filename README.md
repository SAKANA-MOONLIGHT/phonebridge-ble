# PhoneBridge

Experimental Bluetooth-only iPhone pointer/keyboard control and screen streaming for Windows 11. The Windows program runs with the current user's permissions. Device support depends on the Bluetooth adapter, driver, operating system, and device policy.

## Status

The Windows HID path has been exercised with a real iPhone. Screen streaming is experimental and has not yet been built, installed, or validated on an iPhone. No frame-rate, quality, latency, or general compatibility guarantee is made.

The receiver accepts bounded, checksummed JPEG frames over an encrypted BLE GATT write characteristic. The iPhone companion includes a ReplayKit Broadcast Upload Extension for system-wide capture. It does not transmit audio. Pointer control currently uses relative movement, not absolute touch coordinates or multitouch.

## Windows build

Run on Windows with the built-in .NET Framework 4.8 compiler:

```powershell
.\scripts\fetch-contracts.ps1
.\build.ps1 -Test
```

The script downloads pinned Microsoft SDK compile-time references into `.build`. It does not install a driver, service, or global development environment. Copy `dist/PhoneBridge.exe` and `dist/PhoneBridge.exe.config` together to use the program. See [开始使用](开始使用.md).

## iPhone build

Generate the checked-in Xcode project:

```sh
node scripts/generate-ios-project.cjs
node scripts/check-source.cjs
```

The manually triggered GitHub Actions workflow uses the standard `macos-15` runner, checks portable Swift protocol/lifecycle code, and builds an unsigned iPhone app. It does not use Apple account credentials, signing certificates, or paid/larger runners. An unsigned IPA cannot be installed directly: signing the app and its broadcast extension for a personal device remains a separate step.

Free personal provisioning has limitations, including seven-day profile expiry. Installing and refreshing this companion with a free account still requires real-device validation. Source checks and synthetic protocol checks do not prove system-wide screen streaming.

## Source layout

- `windows`: ordinary-user desktop UI, BLE GATT receiver, HOGP keyboard/mouse, frame validation.
- `ios/PhoneBridge`: companion UI, Bluetooth permission and system broadcast picker.
- `ios/Broadcast`: screen sampling, frame lifecycle, JPEG encoding and acknowledged BLE transfer.
- `ios/Tests`: portable protocol and lifecycle checks for an Apple toolchain.
- `docs/蓝牙画面协议.md`: shared packet format and bounds.

## References

- [Apple pointer devices and AssistiveTouch](https://support.apple.com/en-au/111775)
- [Apple personal development accounts](https://developer.apple.com/help/account/basics/about-your-developer-account)
- [Microsoft BLE GATT server](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server)
- [GitHub standard hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
