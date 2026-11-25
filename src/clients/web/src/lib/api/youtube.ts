import type { components } from './schema';
import { SESSION_TOKEN_HEADER } from '../signalr/protocol';

/**
 * REST client for the zero-key YouTube surface (`/api/youtube/*`). The generated OpenAPI schema
 * owns the wire shape; this module only adds friendly error classification, a timeout, and a
 * session-scoped queue-item → request map so a Failed row can be retried with the same videoId.
 */

export type YouTubeSearchResult = components['schemas']['YouTubeSearchResultDto'];
export type YouTubeEnqueueRequest = components['schemas']['YouTubeEnqueueRequest'];
export type YouTubeQueueAck = components['schemas']['QueueAck'];
type ProblemDetails = components['schemas']['ProblemDetails'];

export type YouTubeErrorKind =
  | 'disabled'
  | 'invalid'
  | 'not-found'
  | 'timeout'
  | 'network'
  | 'search-failed'
  | 'failed';

export class YouTubeApiError extends Error {
  readonly kind: YouTubeErrorKind;
  readonly status: number | null;

  constructor(kind: YouTubeErrorKind, message: string, status: number | null = null) {
    super(message);
    this.name = 'YouTubeApiError';
    this.kind = kind;
    this.status = status;
  }
}

const SEARCH_TIMEOUT_MS = 15_000;
const ENQUEUE_TIMEOUT_MS = 15_000;

/** Session-scoped mapping used only for "Retry" on a failed queue row. */
const rememberedRequests = new Map<string, YouTubeEnqueueRequest>();

export function rememberYouTubeRequest(
  queueItemId: string,
  request: YouTubeEnqueueRequest,
): void {
  if (queueItemId) {
    rememberedRequests.set(queueItemId, request);
  }
}

export function getYouTubeRequest(queueItemId: string): YouTubeEnqueueRequest | null {
  return rememberedRequests.get(queueItemId) ?? null;
}

export function isKnownYouTubeQueueItem(queueItemId: string): boolean {
  return rememberedRequests.has(queueItemId);
}

export function forgetYouTubeRequest(queueItemId: string): void {
  rememberedRequests.delete(queueItemId);
}

function friendlyMessage(error: YouTubeApiError): string {
  switch (error.kind) {
    case 'disabled':
      return 'YouTube search and requests are turned off for this stage.';
    case 'timeout':
      return 'YouTube took too long to respond. Try again in a moment.';
    case 'network':
      return 'Could not reach the stage server. Check your connection and try again.';
    case 'search-failed':
      return 'YouTube search is unavailable right now. Try again shortly.';
    case 'invalid':
      return error.message || 'That video cannot be added.';
    case 'not-found':
      return 'That room is no longer live. Ask the stage for a new code.';
    default:
      return error.message || 'The video could not be added to the queue.';
  }
}

/** Human-friendly text for any error thrown by this module. */
export function youtubeErrorMessage(error: unknown): string {
  if (error instanceof YouTubeApiError) {
    return friendlyMessage(error);
  }

  if (error instanceof Error && error.name === 'AbortError') {
    return friendlyMessage(new YouTubeApiError('timeout', ''));
  }

  return 'Something went wrong talking to YouTube. Try again.';
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  try {
    return (await response.json()) as ProblemDetails;
  } catch {
    return null;
  }
}

function classify(response: Response, problem: ProblemDetails | null, search: boolean): YouTubeApiError {
  const detail = problem?.detail?.trim();
  const title = problem?.title?.trim();
  const message = detail || title || `HTTP ${response.status}`;

  if (response.status === 403) {
    return new YouTubeApiError('disabled', message, response.status);
  }

  if (response.status === 400) {
    return new YouTubeApiError('invalid', message, response.status);
  }

  if (response.status === 404) {
    return new YouTubeApiError('not-found', message, response.status);
  }

  if (search && (response.status === 502 || response.status === 504)) {
    return new YouTubeApiError('search-failed', message, response.status);
  }

  return new YouTubeApiError('failed', message, response.status);
}

interface RequestOptions {
  timeoutMs: number;
  search: boolean;
}

async function request<T>(
  input: string,
  init: RequestInit,
  options: RequestOptions,
): Promise<T> {
  const controller = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, options.timeoutMs);

  try {
    let response: Response;
    try {
      response = await fetch(input, { ...init, signal: controller.signal });
    } catch (error) {
      if (timedOut) {
        throw new YouTubeApiError('timeout', 'YouTube request timed out');
      }

      throw new YouTubeApiError(
        'network',
        error instanceof Error ? error.message : 'Network error',
      );
    }

    if (!response.ok) {
      throw classify(response, await readProblem(response), options.search);
    }

    return (await response.json()) as T;
  } finally {
    clearTimeout(timer);
  }
}

/** Searches YouTube through the stage REST API. Empty queries never hit the network. */
export async function searchYouTube(
  query: string,
  maxResults = 10,
  sessionToken?: string | null,
): Promise<YouTubeSearchResult[]> {
  const trimmed = query.trim();
  if (trimmed.length === 0) {
    return [];
  }

  const params = new URLSearchParams({ query: trimmed, maxResults: String(maxResults) });
  const headers = sessionToken ? { [SESSION_TOKEN_HEADER]: sessionToken } : undefined;
  return request<YouTubeSearchResult[]>(
    `/api/youtube/search?${params.toString()}`,
    { method: 'GET', headers },
    { timeoutMs: SEARCH_TIMEOUT_MS, search: true },
  );
}

/** Enqueues one search result for the room and remembers it for a same-session retry. */
export async function enqueueYouTube(
  requestBody: YouTubeEnqueueRequest,
  sessionToken?: string | null,
): Promise<YouTubeQueueAck> {
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (sessionToken) {
    headers[SESSION_TOKEN_HEADER] = sessionToken;
  }

  const ack = await request<YouTubeQueueAck>(
    '/api/youtube/enqueue',
    {
      method: 'POST',
      headers,
      body: JSON.stringify(requestBody),
    },
    { timeoutMs: ENQUEUE_TIMEOUT_MS, search: false },
  );

  if (ack.success) {
    rememberYouTubeRequest(ack.queueItemId, requestBody);
  } else {
    throw new YouTubeApiError('failed', ack.reason ?? 'The queue rejected this video.');
  }

  return ack;
}
