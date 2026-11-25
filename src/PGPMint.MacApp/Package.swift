// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "PGPMintMacApp",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "PGPMintMacCore", targets: ["PGPMintMacCore"]),
        .executable(name: "PGPMint", targets: ["PGPMintMacApp"])
    ],
    targets: [
        .target(
            name: "PGPMintMacCore",
            resources: [
                .copy("Resources")
            ]
        ),
        .executableTarget(
            name: "PGPMintMacApp",
            dependencies: ["PGPMintMacCore"],
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("IOKit")
            ]
        ),
        .testTarget(
            name: "PGPMintMacCoreTests",
            dependencies: ["PGPMintMacCore"]
        )
    ]
)
