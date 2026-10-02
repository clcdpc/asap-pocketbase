export function unconfirmedResponseError() {
    return Object.assign(new Error('The server response did not confirm the workflow result.'), { status: 0 });
  }
