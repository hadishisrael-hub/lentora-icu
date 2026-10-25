# Third-party components

| Component | Version | License | How it is used |
|---|---|---|---|
| [ArgyllCMS](https://www.argyllcms.com/) | 3.5.0 | AGPL-3.0 | Unmodified official Windows binaries; `colprof.exe` is run as a separate process to build the ICC profile. Not stored in this repository. |
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) + runtime.win | 4.10.0.20241108 | Apache-2.0 | Scan decoding, marker detection and perspective correction. |
| [OpenCV](https://opencv.org/) (inside OpenCvSharp4.runtime.win) | 4.10 | Apache-2.0 | Native image processing library. |
| [.NET 8 / WPF](https://github.com/dotnet/wpf) | 8.0 | MIT | Application runtime and UI. |
| [Cal Sans](https://github.com/calcom/font) | Regular | SIL OFL 1.1 | Brand wordmark font; license in `PrinterColorProfiler/Assets/Fonts/OFL-CalSans.txt`. |

## ArgyllCMS corresponding source

Every binary release of Lentora ICU that bundles ArgyllCMS also attaches the matching upstream source archive
(`Argyll_V3.5.0_src.zip`, from <https://www.argyllcms.com/Argyll_V3.5.0_src.zip>) to the same release, and keeps
`Argyll_V3.5.0\License.txt` next to the binaries. ArgyllCMS is Copyright Graeme W. Gill; see
`third-party/ArgyllCMS/ReadMe.txt`.
