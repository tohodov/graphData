export class GraphApi {
  constructor(fetchApi) {
    this.fetchApi = fetchApi;
  }

  async json(url, options = {}) {
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

  fetch(url, options = {}) {
    return this.fetchApi(url, options);
  }
}
