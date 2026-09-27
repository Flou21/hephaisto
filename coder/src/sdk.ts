// The ONLY file that imports @anthropic-ai/claude-agent-sdk. Everything else talks to the SDK
// through the types re-exported here and the QueryFn this module hands out, so the real SDK and
// the scripted fake are interchangeable - and the fake is typed against the real SDKMessage, so
// an SDK bump that changes the message shape breaks the fake's build, not a production run.

import type {
  CanUseTool,
  HookCallbackMatcher,
  HookInput,
  HookJSONOutput,
  Options,
  PermissionResult,
  PostToolUseHookInput,
  PreToolUseHookInput,
  SDKAssistantMessage,
  SDKMessage,
  SDKResultMessage,
  SDKSystemMessage,
  SDKUserMessage,
} from '@anthropic-ai/claude-agent-sdk';
import { fakeQuery, type FakeContext } from './fake-sdk.js';

export type {
  CanUseTool,
  HookCallbackMatcher,
  HookInput,
  HookJSONOutput,
  Options,
  PermissionResult,
  PostToolUseHookInput,
  PreToolUseHookInput,
  SDKAssistantMessage,
  SDKMessage,
  SDKResultMessage,
  SDKSystemMessage,
  SDKUserMessage,
};

export type QueryFn = (params: { prompt: string; options: Options }) => AsyncIterable<SDKMessage>;

export type SdkMode = 'fake' | 'real';

export async function loadQuery(mode: SdkMode, fake: FakeContext | null): Promise<QueryFn> {
  if (mode === 'fake') {
    if (!fake) throw new Error('fake SDK selected without a fake context');
    return (p) => fakeQuery(p, fake);
  }
  const sdk = await import('@anthropic-ai/claude-agent-sdk');
  return (p) => sdk.query(p);
}

/** The pinned Claude Code executable when CODEFIX_CLAUDE_EXECUTABLE is unset: the SDK's own lockstep binary. */
export const SDK_PACKAGE = '@anthropic-ai/claude-agent-sdk';
