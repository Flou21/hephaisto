import { readFileSync } from 'node:fs'

/**
 * The glyph and class vocabulary, read from the C# that owns it.
 *
 * docs/design.md says half the design system is in C#, and names Components/Display.cs as the
 * owner of the glyph vocabulary and the enum-to-class mapping. This file had its own copy, and
 * the copy had drifted: three of the five states it carried were wrong, and each wrong one was
 * ANOTHER state's glyph - Escalated rendered with AwaitingApproval's `!`, Investigating with
 * Detected's `*`, Detected with Expired's `.`. Every page on demo.hephaisto.dev shipped that.
 * It also had no entry at all for Acting, Verifying, Triaging, AwaitingApproval or Expired,
 * and dropped the st-* class the console emits beside the glyph - which is how a page ends up
 * announcing state by colour alone, the one thing docs/design.md rules out.
 *
 * So it is parsed rather than copied, exactly as enums.mjs parses Enums.cs, and it refuses
 * rather than returning something plausible. What actually breaks this is a switch rewritten
 * as a dictionary; a parse that quietly produced an empty table would render every incident
 * identically and look deliberate.
 */

// `IncidentState.Resolved => "+",` - one arm of an expression-bodied switch.
const ARM = /^\s*[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*"([^"]*)"\s*,/
const FALLBACK = /^\s*_\s*=>\s*"([^"]*)"\s*,/

const METHOD = (name) =>
    new RegExp(`public static string ${name}\\([^)]*\\)\\s*=>\\s*\\w+ switch\\s*\\{([\\s\\S]*?)\\n    \\};`, 'm')

/** Each table, and the smallest number of arms that proves the parse still works. */
const TABLES = {
    stateGlyph: { method: 'StateGlyph', least: 10 },
    stateClass: { method: 'StateClass', least: 10 },
    decisionGlyph: { method: 'DecisionGlyph', least: 2 },
    decisionClass: { method: 'DecisionClass', least: 2 },
}

export function read(displayCsPath) {
    const source = readFileSync(displayCsPath, 'utf8')
    const out = {}

    for (const [key, { method, least }] of Object.entries(TABLES)) {
        const block = source.match(METHOD(method))

        if (!block) {
            throw new Error(
                `Display.${method} not found in ${displayCsPath}. It was renamed, or rewritten as `
                + 'something other than an expression-bodied switch - refusing to render pages '
                + 'whose every state would look the same.',
            )
        }

        const arms = {}
        let fallback = null

        for (const line of block[1].split('\n')) {
            const arm = line.match(ARM)

            if (arm) {
                arms[arm[1]] = arm[2]
                continue
            }

            // Deny lives in the default arm rather than being named, so the fallback is part
            // of the vocabulary here and not just defensive.
            const other = line.match(FALLBACK)

            if (other) {
                fallback = other[1]
            }
        }

        if (Object.keys(arms).length < least) {
            throw new Error(
                `Display.${method} parsed to ${Object.keys(arms).length} arms, expected at least `
                + `${least} - the switch syntax has changed.`,
            )
        }

        out[key] = { arms, fallback }
    }

    return out
}

/** A member the table does not name falls back the way the C# does. */
export function look(display, table, member) {
    const found = display[table]

    if (!found) {
        throw new Error(`no ${table} table was parsed`)
    }

    return found.arms[member] ?? found.fallback ?? ''
}
