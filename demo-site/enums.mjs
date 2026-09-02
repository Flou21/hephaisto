import { readFileSync } from 'node:fs'

/**
 * The enum maps, read from the C# that defines them.
 *
 * Transcripts serialize enums as integers - there is no JsonStringEnumConverter in
 * Transcript.Json - so a renderer needs the maps. Hand-copying them into JavaScript would create
 * a second definition that drifts the first time a member is inserted in the middle, and the
 * symptom would be a page confidently labelling every incident with the wrong state.
 *
 * So they are parsed from src/Hephaisto.Core/Domain/Enums.cs at build time instead. A parse that
 * silently produced empty maps would be worse than no maps at all, so `read` refuses rather than
 * returning something plausible - the same contract scripts/brand-assets.sh uses when the font
 * did not load.
 */

const ENUM_BLOCK = (name) =>
    new RegExp(`public enum ${name}\\s*\\{([\\s\\S]*?)\\n\\}`, 'm')

const MEMBER = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(\d+)\s*,?\s*$/

/** Every enum a transcript can contain a value of. */
export const REQUIRED = [
    'SignalSource',
    'SignalKind',
    'Severity',
    'IncidentState',
    'SuppressionReason',
    'EscalationReason',
    'TerminationReason',
    'ActionType',
    'AgentMode',
    'StepKind',

    // The action row: a cluster capture carries a policy decision and an execution state,
    // which no replayed transcript ever had.
    'PolicyDecision',
    'ActionState',
    'ApprovalSource',
    'RiskTier',
]

export function read(enumsCsPath) {
    const source = readFileSync(enumsCsPath, 'utf8')
    const maps = {}

    for (const name of REQUIRED) {
        const block = source.match(ENUM_BLOCK(name))

        if (!block) {
            throw new Error(
                `enum ${name} not found in ${enumsCsPath}. It was renamed, moved, or the file `
                + 'was restructured - refusing to render pages that would label every value wrong.',
            )
        }

        const members = {}

        for (const line of block[1].split('\n')) {
            const m = line.match(MEMBER)
            if (m) {
                members[Number(m[2])] = m[1]
            }
        }

        if (Object.keys(members).length === 0) {
            throw new Error(`enum ${name} parsed to zero members - the member syntax has changed.`)
        }

        maps[name] = members
    }

    return maps
}

/** A value the maps do not cover is shown as what it is, never as a guess. */
export function label(maps, enumName, value) {
    const found = maps[enumName]?.[value]
    return found ?? `${enumName}(${value})`
}
