export type GraphApiError = Error & {
  status?: number;
  statusText?: string;
  responseText?: string;
  url?: string;
  method?: string;
};

export class GraphApi {
  fetchApi: typeof fetch;
  constructor(fetchApi: typeof fetch) {
    this.fetchApi = fetchApi;
  }

  async json<T = unknown>(url: string, options: { method?: string; body?: BodyInit | null; expectJson?: boolean } = {}): Promise<T | null> {
    const response = await this.fetchApi(url, {
      method: options.method ?? "GET",
      headers: { "Content-Type": "application/json" },
      body: options.body
    });
    if (!response.ok) throw await GraphApi.errorFromResponse(response, url, options.method ?? "GET");
    if (options.expectJson === false || response.status === 204) return null;
    return response.json() as Promise<T>;
  }

  fetch(url: string, options: RequestInit = {}): Promise<Response> {
    return this.fetchApi(url, options);
  }

  static async errorFromResponse(response: Response, url: string, method = "GET"): Promise<GraphApiError> {
    const text = await response.text();
    const statusText = response.statusText ? " " + response.statusText : "";
    const error = new Error(text || `HTTP ${response.status}${statusText}`) as GraphApiError;
    error.status = response.status;
    error.statusText = response.statusText;
    error.responseText = text;
    error.url = url;
    error.method = method;
    return error;
  }
}
