import Foundation
import IOKit.pwr_mgt
import PGPMintMacCore

@MainActor
final class MacPowerActivityManager: PowerActivityManaging {
    private var activityToken: NSObjectProtocol?
    private var sleepAssertionId: IOPMAssertionID = 0
    private var hasSleepAssertion = false

    var sleepProtected: Bool { hasSleepAssertion }

    func setActive(_ active: Bool) {
        if active {
            acquire()
        } else {
            release()
        }
    }

    private func acquire() {
        if activityToken == nil {
            // Prevent macOS App Nap and background timer coalescing while GPU mining is active.
            activityToken = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .latencyCritical],
                reason: "PGPMint hardware GPU vanity Key ID search"
            )
        }
        if !hasSleepAssertion {
            var assertionId: IOPMAssertionID = 0
            let reason = "PGPMint hardware GPU search" as CFString
            let assertionType = kIOPMAssertionTypePreventUserIdleSystemSleep as CFString
            let status = IOPMAssertionCreateWithName(
                assertionType,
                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                reason,
                &assertionId
            )
            if status == kIOReturnSuccess {
                sleepAssertionId = assertionId
                hasSleepAssertion = true
            }
        }
    }

    private func release() {
        if let token = activityToken {
            ProcessInfo.processInfo.endActivity(token)
            activityToken = nil
        }
        if hasSleepAssertion {
            IOPMAssertionRelease(sleepAssertionId)
            sleepAssertionId = 0
            hasSleepAssertion = false
        }
    }
}
