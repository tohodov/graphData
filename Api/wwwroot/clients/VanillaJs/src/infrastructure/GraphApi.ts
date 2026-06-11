export class GraphApi {
  fetchApi: typeof fetch;
  constructor(fetchApi: typeof fetch) {
    this.fetchApi = fetchApi;
  }

  async json(url: string, options: { method?: string; body?: BodyInit | null; expectJson?: boolean } = {}): Promise<unknown> {
    const response = await this.fetchApi(url, {
      method: options.method ?? "GET",
      headers: { "Content-Type": "application/json" },
      body: options.body
    });
    if (!response.ok) {
      const text = await response.text();
      const error = new Error(text || "HTTP " + response.status);
      error.status = response.status;
      throw error;
    }
    if (options.expectJson === false || response.status === 204) return null;
    return response.json();
  }

  fetch(url: string, options: RequestInit = {}): Promise<Response> {
    return this.fetchApi(url, options);
  }
}
