import { Suspense } from "react";
import { headers } from "next/headers";
import { requireSession } from "@/lib/session";
import { enabledProviders, getAuth } from "@/lib/auth";
import { AccountSettings } from "@/components/auth/account-settings";
import { Navbar } from "@/components/ui/layout/Navbar";
import { Footer } from "@/components/ui/layout/Footer";
import { AccountPageSkeleton } from "@/components/loading";
export const metadata = { title: "บัญชีของฉัน | KamenRaiderPlay" };

async function Account() {
  const session = await requireSession();
  const auth = await getAuth();
  const accounts = await auth.api.listUserAccounts({
    headers: await headers(),
  });
  return (
    <AccountSettings
      user={{
        id: session.user.id,
        name: session.user.name,
        email: session.user.email,
        image: session.user.image,
        balanceSatang: session.user.balanceSatang,
        role: session.user.role,
      }}
      initialAccounts={accounts.map((a) => ({
        id: a.id,
        providerId: a.providerId,
      }))}
      providers={enabledProviders}
    />
  );
}

export default function AccountPage() {
  return (
    <div className="flex min-h-screen flex-col">
      <Navbar />
      <main className="mx-auto w-full max-w-xl flex-1 px-5 pt-32 pb-16">
        <Suspense fallback={<AccountPageSkeleton />}>
          <Account />
        </Suspense>
      </main>
      <Footer />
    </div>
  );
}
